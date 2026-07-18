// SPDX-License-Identifier: 0BSD
//
// ip_udp_send.c — send one UDP datagram to a peer, using nothing but the standard
// Berkeley sockets API. There is ZERO packet-radio or AX.25 awareness in this file.
//
// The entire point of this program is that it is *ordinary IP code*. It does not know,
// and cannot tell, that the datagram it sends will travel over an amateur radio link.
// It works over radio only because `pdn-net` has brought up a `pdn0` TUN interface and
// the host's routing table sends 44-net (AMPRNet) traffic to it — pdn-net then carries
// each IP packet as an AX.25 UI frame to the callsign mapped to the destination address.
//
// Swap this program for `ping`, `ssh`, `mosh`, `mqtt`, `nc`, `git` — anything that speaks
// IP. None of them change. That is the whole proposition of the tun/IP seam.
//
// Build:   cc -Wall -Wextra -o ip_udp_send ip_udp_send.c
// Run:     ./ip_udp_send 44.0.0.2 5555 "hello over radio"
//
// (First bring up pdn0 — see samples/README.md. Then run this against a 44-net peer.)

#include <arpa/inet.h>   // inet_pton, htons
#include <netinet/in.h>  // sockaddr_in
#include <stdio.h>
#include <stdlib.h>      // atoi
#include <string.h>
#include <sys/socket.h>  // socket, sendto
#include <unistd.h>      // close

int main(int argc, char **argv)
{
    if (argc < 3) {
        fprintf(stderr, "usage: %s <dest-ip> <dest-port> [message]\n", argv[0]);
        fprintf(stderr, "example: %s 44.0.0.2 5555 \"hello over radio\"\n", argv[0]);
        return 2;
    }

    const char *dest_ip   = argv[1];
    const int   dest_port = atoi(argv[2]);
    const char *message   = (argc > 3) ? argv[3] : "hello over radio";

    // A plain IPv4 UDP socket. Nothing radio-specific — the kernel picks the route,
    // and for a 44-net destination that route points at pdn0.
    int fd = socket(AF_INET, SOCK_DGRAM, 0);
    if (fd < 0) {
        perror("socket");
        return 1;
    }

    struct sockaddr_in dest;
    memset(&dest, 0, sizeof(dest));
    dest.sin_family = AF_INET;
    dest.sin_port   = htons((unsigned short)dest_port);
    if (inet_pton(AF_INET, dest_ip, &dest.sin_addr) != 1) {
        fprintf(stderr, "bad destination IP: %s\n", dest_ip);
        close(fd);
        return 1;
    }

    ssize_t n = sendto(fd, message, strlen(message), 0,
                       (struct sockaddr *)&dest, sizeof(dest));
    if (n < 0) {
        // "Network is unreachable" here usually means pdn0 isn't up or the route to
        // 44.0.0.0/8 isn't installed — see samples/README.md.
        perror("sendto");
        close(fd);
        return 1;
    }

    printf("sent %zd bytes to %s:%d over standard IP "
           "(which, thanks to pdn0, means over the radio)\n", n, dest_ip, dest_port);

    close(fd);
    return 0;
}
