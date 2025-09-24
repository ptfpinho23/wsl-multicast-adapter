using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

class WslMcastSvcTcp
{
    const int TCP_PORT = 5000;                 // TCP from Linux mcast0 driver
    const int BUFFER_SIZE = 2048;
    const string MCAST_ADDR = "239.1.1.1";     // Example multicast group
    const int MCAST_PORT = 6000;               // Example UDP multicast port

    static void Main()
    {
        // Start UDP multicast listener on Windows
        var mcastClient = new UdpClient(MCAST_PORT, AddressFamily.InterNetwork);
        mcastClient.JoinMulticastGroup(IPAddress.Parse(MCAST_ADDR));
        Console.WriteLine($"[udp] Joined multicast {MCAST_ADDR}:{MCAST_PORT}");

        // Accept TCP connection from WSL kernel module
        var listener = new TcpListener(IPAddress.Any, TCP_PORT);
        listener.Start();
        Console.WriteLine($"[tcp] Listening on 0.0.0.0:{TCP_PORT} ...");

        using var conn = listener.AcceptTcpClient();
        Console.WriteLine("[tcp] Connected from WSL.");
        var tcpStream = conn.GetStream();

        // Two directions:
        // 1) TCP→UDP (frames from Linux → Windows multicast)
        var tcpToUdp = Task.Run(() =>
        {
            var buf = new byte[BUFFER_SIZE];
            while (true)
            {
                int n = tcpStream.Read(buf, 0, buf.Length);
                if (n <= 0) break;

                if (n >= 2)
                {
                    ushort framelen = BitConverter.ToUInt16(buf, 0);
                    if (framelen > 0 && framelen <= n - 2)
                    {
                        byte[] frame = new byte[framelen];
                        Array.Copy(buf, 2, frame, 0, framelen);

                        // Forward to UDP multicast
                        mcastClient.Send(frame, frame.Length,
                            new IPEndPoint(IPAddress.Parse(MCAST_ADDR), MCAST_PORT));

                        Console.WriteLine($"[tcp→udp] Forwarded {framelen} bytes");
                    }
                }
            }
        });

        // 2) UDP→TCP (multicast packets on Windows → forward to Linux mcast0)
        var udpToTcp = Task.Run(() =>
        {
            IPEndPoint? src = null;
            while (true)
            {
                var data = mcastClient.Receive(ref src);
                if (data == null || data.Length == 0)
                    continue;

                ushort len = (ushort)data.Length;
                byte[] framed = new byte[2 + data.Length];
                Array.Copy(BitConverter.GetBytes(len), framed, 2);
                Array.Copy(data, 0, framed, 2, data.Length);

                tcpStream.Write(framed, 0, framed.Length);
                Console.WriteLine($"[udp→tcp] Forwarded {data.Length} bytes from {src}");
            }
        });

        Task.WaitAll(tcpToUdp, udpToTcp);
        Console.WriteLine("[tcp] Disconnected.");
    }
}
