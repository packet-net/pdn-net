using System.Runtime.InteropServices;

namespace Packet.Net.Tun;

/// <summary>
/// A Linux TUN interface over raw <c>libc</c> P/Invoke (<c>open</c> / <c>ioctl(TUNSETIFF)</c> /
/// <c>read</c> / <c>write</c> / <c>close</c>) — the layer-3 counterpart of pdn-soundmodem's
/// libasound PCM wrapper. Linux-only by design (the headless Pi/x64 node is the deployment);
/// a cross-platform backend (utun on macOS, Wintun on Windows) can join behind
/// <see cref="ITunDevice"/> later.
/// </summary>
/// <remarks>
/// <para><b>Privileges.</b> Opening <c>/dev/net/tun</c> and issuing <c>TUNSETIFF</c> needs
/// <c>CAP_NET_ADMIN</c>. Run the daemon as root, or grant the capability once:
/// <c>sudo setcap cap_net_admin+ep $(which pdn-net)</c> (or the published binary path).
/// <see cref="Open"/> throws a clear <see cref="TunDeviceException"/> when it can't — the daemon
/// turns that into a friendly startup error rather than a stack trace.</para>
/// <para><b>Async model.</b> <c>read</c>/<c>write</c> on the TUN fd are blocking; the async
/// methods offload them to the thread pool. A pending <see cref="ReadPacketAsync"/> is released
/// by <see cref="Dispose"/> closing the fd (which makes the blocking <c>read</c> return), not by
/// the <see cref="CancellationToken"/> — the token only prevents the work from starting. Fine for
/// a byte-starved radio channel; a real epoll loop is a later optimisation.</para>
/// </remarks>
public sealed class TunDevice : ITunDevice
{
    private const string Libc = "libc";

    // <fcntl.h>
    private const int O_RDWR = 0x0002;

    // <linux/if_tun.h> — TUNSETIFF request code and interface flags.
    // IFF_TUN: layer-3 (IP) mode. IFF_NO_PI: no 4-byte packet-info prefix, so read/write are
    // bare IP packets (the bridge parses the IPv4 header at offset 0).
    private const uint TUNSETIFF = 0x400454ca;
    private const short IFF_TUN = 0x0001;
    private const short IFF_NO_PI = 0x1000;

    private const int IfNameSize = 16; // IFNAMSIZ

    private int _fd;
    private int _disposed;

    private TunDevice(int fd, string name)
    {
        _fd = fd;
        Name = name;
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <summary>
    /// Opens <c>/dev/net/tun</c> and creates (or attaches to) a TUN interface with the requested
    /// name. The kernel may substitute a name (e.g. when the request collides); the actual name is
    /// exposed as <see cref="Name"/>.
    /// </summary>
    /// <param name="requestedName">Desired interface name, e.g. <c>pdn0</c> (max 15 chars).</param>
    /// <exception cref="PlatformNotSupportedException">Not running on Linux.</exception>
    /// <exception cref="TunDeviceException">The device could not be opened or configured.</exception>
    public static unsafe TunDevice Open(string requestedName = "pdn0")
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException(
                "TunDevice uses /dev/net/tun and is Linux-only. Use a fake ITunDevice off-Linux.");

        if (requestedName.Length >= IfNameSize)
            throw new ArgumentException(
                $"Interface name '{requestedName}' exceeds {IfNameSize - 1} characters.", nameof(requestedName));

        int fd = open("/dev/net/tun", O_RDWR);
        if (fd < 0)
        {
            int err = Marshal.GetLastPInvokeError();
            throw new TunDeviceException(
                $"open(/dev/net/tun) failed (errno {err}: {Strerror(err)}). " +
                "Is the tun module loaded and /dev/net/tun present? " +
                "Opening it needs CAP_NET_ADMIN — run as root or `setcap cap_net_admin+ep` the binary.");
        }

        // struct ifreq { char ifr_name[IFNAMSIZ]; union { short ifr_flags; ... } }; total 40 bytes.
        // We only populate name + flags for TUNSETIFF; the rest of the union stays zero.
        var ifr = default(IfReq);
        byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(requestedName);
        for (int i = 0; i < nameBytes.Length && i < IfNameSize - 1; i++)
            ifr.Name[i] = nameBytes[i];
        ifr.Flags = unchecked((short)(IFF_TUN | IFF_NO_PI));

        int rc = ioctl(fd, TUNSETIFF, ref ifr);
        if (rc < 0)
        {
            int err = Marshal.GetLastPInvokeError();
            close(fd);
            throw new TunDeviceException(
                $"ioctl(TUNSETIFF, {requestedName}) failed (errno {err}: {Strerror(err)}). " +
                "This needs CAP_NET_ADMIN — run as root or `setcap cap_net_admin+ep` the binary.");
        }

        // Read the (possibly kernel-substituted) name back out of the struct.
        int len = 0;
        while (len < IfNameSize && ifr.Name[len] != 0) len++;
        string actualName = System.Text.Encoding.ASCII.GetString(
            new ReadOnlySpan<byte>(ifr.Name, len).ToArray());

        return new TunDevice(fd, actualName.Length == 0 ? requestedName : actualName);
    }

    /// <inheritdoc/>
    public ValueTask<int> ReadPacketAsync(Memory<byte> buffer, CancellationToken ct = default)
        => new(Task.Run(() =>
        {
            using var pin = buffer.Pin();
            return ReadInto(pin, buffer.Length);
        }, ct));

    /// <inheritdoc/>
    public ValueTask WritePacketAsync(ReadOnlyMemory<byte> packet, CancellationToken ct = default)
        => new(Task.Run(() =>
        {
            using var pin = packet.Pin();
            WriteFrom(pin, packet.Length);
        }, ct));

    private unsafe int ReadInto(System.Buffers.MemoryHandle pin, int length)
    {
        nint n = read(_fd, (byte*)pin.Pointer, (nuint)length);
        if (n < 0)
        {
            int err = Marshal.GetLastPInvokeError();
            if (Volatile.Read(ref _disposed) != 0) return 0; // closed under us — treat as EOS
            throw new TunDeviceException($"read(tun) failed (errno {err}: {Strerror(err)}).");
        }
        return (int)n;
    }

    private unsafe void WriteFrom(System.Buffers.MemoryHandle pin, int length)
    {
        nint n = write(_fd, (byte*)pin.Pointer, (nuint)length);
        if (n < 0)
        {
            int err = Marshal.GetLastPInvokeError();
            throw new TunDeviceException($"write(tun) failed (errno {err}: {Strerror(err)}).");
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        int fd = Interlocked.Exchange(ref _fd, -1);
        if (fd >= 0) close(fd);
    }

    private static string Strerror(int err)
    {
        IntPtr p = strerror(err);
        return p == IntPtr.Zero ? $"errno {err}" : Marshal.PtrToStringAnsi(p) ?? $"errno {err}";
    }

    // struct ifreq (Linux, 64-bit): char[16] name; then a 24-byte union whose first member here is
    // a short (ifr_flags). Fixed 40-byte layout so the ioctl argument is the exact kernel shape.
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct IfReq
    {
        public fixed byte Name[IfNameSize];
        public short Flags;
        private fixed byte _pad[22];
    }

    [DllImport(Libc, SetLastError = true)]
    private static extern int open(string pathname, int flags);

    [DllImport(Libc, SetLastError = true)]
    private static extern int close(int fd);

    [DllImport(Libc, SetLastError = true)]
    private static extern int ioctl(int fd, uint request, ref IfReq argp);

    [DllImport(Libc, SetLastError = true)]
    private static extern unsafe nint read(int fd, byte* buf, nuint count);

    [DllImport(Libc, SetLastError = true)]
    private static extern unsafe nint write(int fd, byte* buf, nuint count);

    [DllImport(Libc)]
    private static extern IntPtr strerror(int errnum);
}

/// <summary>Raised when a TUN device cannot be opened, configured, or used.</summary>
public sealed class TunDeviceException : Exception
{
    /// <summary>Creates the exception with a human-readable, operator-actionable message.</summary>
    public TunDeviceException(string message) : base(message) { }
}
