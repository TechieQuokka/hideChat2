using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace hideChat2
{
    /// <summary>
    /// SOCKS5 client for connecting to .onion addresses through Tor
    /// </summary>
    public static class Socks5Client
    {
        public static async Task<NetworkStream> ConnectAsync(
            string socksHost, int socksPort,
            string targetHost, int targetPort,
            CancellationToken ct = default)
        {
            var tcp = new TcpClient();

            // Increase timeout for .onion connections (can take 30-60 seconds)
            tcp.ReceiveTimeout = 120000; // 120 seconds
            tcp.SendTimeout = 120000;    // 120 seconds

            await tcp.ConnectAsync(socksHost, socksPort);
            var stream = tcp.GetStream();

            // Set stream timeouts as well
            stream.ReadTimeout = 120000;
            stream.WriteTimeout = 120000;

            // SOCKS5 handshake
            // 1) Authentication negotiation: VER=5, NMETHODS=1, METHOD=0(No Auth)
            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, 0, 3, ct);

            var resp = new byte[2];
            await ReadExactAsync(stream, resp, ct);
            if (resp[0] != 0x05 || resp[1] != 0x00)
                throw new Exception("SOCKS5 authentication negotiation failed");

            // 2) Connection request: VER=5, CMD=CONNECT, RSV=0, ATYP=3(domain)
            var hostBytes = Encoding.ASCII.GetBytes(targetHost);
            var portBytes = new byte[] { (byte)(targetPort >> 8), (byte)(targetPort & 0xFF) };

            var request = new List<byte>
            {
                0x05, 0x01, 0x00, 0x03,
                (byte)hostBytes.Length
            };
            request.AddRange(hostBytes);
            request.AddRange(portBytes);

            await stream.WriteAsync(request.ToArray(), 0, request.Count, ct);

            // 3) Read response: VER(1) + REP(1) + RSV(1) + ATYP(1)
            var respHeader = new byte[4];
            await ReadExactAsync(stream, respHeader, ct);

            if (respHeader[1] != 0x00)
                throw new Exception($"SOCKS5 connection failed: REP=0x{respHeader[1]:X2}");

            // Read remaining bytes based on ATYP to drain the response correctly
            byte atyp = respHeader[3];
            int addrLen;
            if (atyp == 0x01)       addrLen = 4;   // IPv4
            else if (atyp == 0x04)  addrLen = 16;  // IPv6
            else if (atyp == 0x03)
            {
                var lenBuf = new byte[1];
                await ReadExactAsync(stream, lenBuf, ct);
                addrLen = lenBuf[0]; // domain name length
            }
            else throw new Exception($"SOCKS5 unknown address type: 0x{atyp:X2}");

            var addrPort = new byte[addrLen + 2]; // BND.ADDR + BND.PORT(2)
            await ReadExactAsync(stream, addrPort, ct);

            return stream;
        }

        private static async Task ReadExactAsync(NetworkStream stream, byte[] buf, CancellationToken ct)
        {
            int total = 0;
            while (total < buf.Length)
            {
                int read = await stream.ReadAsync(buf, total, buf.Length - total, ct);
                if (read == 0) throw new Exception("Connection closed unexpectedly");
                total += read;
            }
        }
    }
}
