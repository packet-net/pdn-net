using Microsoft.Extensions.Logging;
using Packet.Net.Bridge;
using Packet.Net.Rhp;
using Packet.Net.Routing;
using Packet.Net.Tun;

namespace Packet.Net.Daemon;

/// <summary>
/// The pdn-net daemon: brings up a <c>pdn0</c> TUN interface and bridges IP ⇄ AX.25 UI datagrams
/// through a pdn node over RHPv2. Ordinary, unmodified Linux IP software (ssh, mosh, mqtt, ping…)
/// runs over packet radio because its packets are routed to this interface.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string configPath = ParseConfigPath(args) ?? "pdn-net.json";

        using var loggerFactory = LoggerFactory.Create(b => b
            .AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            })
            .SetMinimumLevel(LogLevel.Debug));
        var log = loggerFactory.CreateLogger("pdn-net");

        PdnNetConfig config;
        try
        {
            config = PdnNetConfig.Load(configPath);
        }
        catch (Exception ex)
        {
            log.LogError("Could not load config '{Path}': {Message}", configPath, ex.Message);
            return 1;
        }

        var (rhpHost, rhpPort) = config.RhpEndpoint();
        log.LogInformation("pdn-net starting — config {Path}", configPath);
        log.LogInformation("  callsign {Callsign}, tun {Tun}, mtu {Mtu}, {Routes} route(s), node {Host}:{Port}",
            config.MyCallsign, config.TunName, config.Mtu, config.Routes.Count, rhpHost, rhpPort);

        // Ctrl+C / SIGTERM → graceful stop. Guard Cancel() so a late ProcessExit (after the CTS is
        // disposed on the way out) doesn't throw ObjectDisposedException.
        using var cts = new CancellationTokenSource();
        void SafeCancel()
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { /* already shutting down */ }
        }
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; SafeCancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SafeCancel();

        // 1) TUN interface — fails gracefully with an actionable message if we lack privileges.
        TunDevice tun;
        try
        {
            tun = TunDevice.Open(config.TunName);
        }
        catch (Exception ex) when (ex is TunDeviceException or PlatformNotSupportedException)
        {
            log.LogError("Could not bring up the TUN interface: {Message}", ex.Message);
            return 1;
        }
        log.LogInformation("Interface up: {Name}", tun.Name);
        log.LogInformation("  configure it with: sudo ip addr add <your-ip>/32 dev {Name} && " +
                           "sudo ip link set {Name2} up mtu {Mtu}", tun.Name, tun.Name, config.Mtu);

        // 2) RHP custom client — connect + bind our callsign.
        await using var rhp = new RhpCustomClient(rhpHost, rhpPort, loggerFactory.CreateLogger<RhpCustomClient>());
        try
        {
            await rhp.ConnectAsync(cts.Token);
            await rhp.BindAsync(config.MyCallsign, cts.Token);
        }
        catch (OperationCanceledException)
        {
            tun.Dispose();
            return 0;
        }
        catch (Exception ex)
        {
            log.LogError("Could not connect to the pdn node at {Host}:{Port}: {Message}. " +
                         "Is the node running with RHP enabled?", rhpHost, rhpPort, ex.Message);
            tun.Dispose();
            return 1;
        }

        // 3) The bridge.
        byte[]? myIpBytes = config.MyIp is not null
            ? System.Net.IPAddress.Parse(config.MyIp).GetAddressBytes()
            : null;
        var bridge = new IpAx25Bridge(tun, rhp, CallsignResolver.FromConfig(config),
            config.Mtu, config.MyCallsign, myIpBytes,
            log: loggerFactory.CreateLogger<IpAx25Bridge>());

        log.LogInformation("Bridge running — IP ⇄ AX.25 UI (pid 0x{Pid:X2}). Ctrl+C to stop.", IpAx25Bridge.PidIp);
        try
        {
            await bridge.RunAsync(cts.Token);
        }
        catch (OperationCanceledException) { /* expected on stop */ }
        finally
        {
            tun.Dispose();
            log.LogInformation("pdn-net stopped.");
        }
        return 0;
    }

    private static string? ParseConfigPath(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] is "--config" or "-c")
                return args[i + 1];
        return null;
    }
}
