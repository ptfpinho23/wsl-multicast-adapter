using System;
using System.Net;
using System.Net.Sockets;

class WslMcastSvcTcp
{
    const int PORT = 5000;
    const int BUFFER_SIZE = 2048;

    static void Main()
    {
        var ep = new IPEndPoint(IPAddress.Any, PORT);
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        // Allow quick restarts
        listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        listener.Bind(ep);
        listener.Listen(1);

        Console.WriteLine($"[tcp] Listening on 127.0.0.1:{PORT} ...");
        using var conn = listener.Accept();
        Console.WriteLine("[tcp] Connected from WSL.");

        var buf = new byte[BUFFER_SIZE];

        while (true)
        {
            int n = conn.Receive(buf);
            if (n <= 0) break;

            // Expect [2-byte length][payload]
            if (n >= 2)
            {
                ushort framelen = BitConverter.ToUInt16(buf, 0);
                Console.WriteLine($"[tcp] RX frame {framelen} bytes");

                // Optional: echo the same frame back so Linux RX path sees traffic
                conn.Send(buf, 0, Math.Min(2 + framelen, n), SocketFlags.None);
            }
        }

        Console.WriteLine("[tcp] Disconnected.");
    }
}
