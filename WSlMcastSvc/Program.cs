using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;


// AF_VSOCK - since .Net doesn't ship with official vsock bindings?

public enum VsockAddressFamily : ushort
{
    AF_VSOCK = 40
}

public struct SockAddrVsock
{
    public ushort Family;   // AF_VSOCK
    public uint Cid;        // CID (context id - for VSOCK)
    public uint Port;       // Port number
}

public class VsockEndPoint : EndPoint
{
    public uint Cid { get; }
    public uint Port { get; }

    public VsockEndPoint(uint Cid, uint port)
    {
        this.Cid = Cid;
        this.Port = port;
    }

    public override AddressFamily AddressFamily => (AddressFamily)VsockAddressFamily.AF_VSOCK;

    public override SocketAddress Serialize()
    {
        var socket_addr = new SocketAddress(AddressFamily, 16); // 16 byte sockaddr

        ushort fam = (ushort)AddressFamily;

        socket_addr[0] = (byte)fam; //  low byte - (x86)
        socket_addr[1] = (byte)(fam >> 8); // high byte portion




        byte[] cidBytes = BitConverter.GetBytes(Cid);
        for (int i = 0; i < cidBytes.Length; i++)
        {
            socket_addr[4 + i] = cidBytes[i];
        }

        byte[] portBytes = BitConverter.GetBytes(Port);
        for (int i = 0; i < portBytes.Length; i++)
        {
            socket_addr[8 + i] = portBytes[i];
        }

        return socket_addr;

    }
}

public static class Vsock
{
    public const uint CID_ANY = 0xFFFFFFFF; // wildcard
    public const uint CID_HOST = 2; // host CID
}


class WslMcastSvc
{
    const int VSOCK_PORT = 12345;
    const int UDP_PORT = 5000;
    const string MCAST_GROUP = "224.1.1.1";

    const int BUFFER_SIZE = 2048;

    static void Main(string[] args)
    {
        Console.WriteLine("Starting Windows Multicast Svc...");

        // 1. Open vsock listener
        var vsock = new Socket((AddressFamily)VsockAddressFamily.AF_VSOCK,
                               SocketType.Stream,
                               ProtocolType.Unspecified);
        vsock.Bind(new VsockEndPoint(Vsock.CID_ANY, VSOCK_PORT));
        vsock.Listen(1);

        Console.WriteLine($"Waiting for vsock connection on port {VSOCK_PORT}...");
        var conn = vsock.Accept();
        Console.WriteLine("Connected to WSL kernel module.");

        // 2. Open UDP multicast socket
        var udp = new UdpClient(UDP_PORT);
        udp.JoinMulticastGroup(IPAddress.Parse(MCAST_GROUP));
        Console.WriteLine($"Successfully joined multicast group {MCAST_GROUP} on port {UDP_PORT}");

        var cancel = new CancellationTokenSource();

        // 3. WSL → LAN
        Task.Run(() =>
        {
            var buf = new byte[BUFFER_SIZE];
            while (!cancel.Token.IsCancellationRequested)
            {
                int len = conn.Receive(buf);
                if (len > 0)
                {
                    udp.Send(buf, len, new IPEndPoint(IPAddress.Parse(MCAST_GROUP), UDP_PORT));
                    Console.WriteLine($"Forwarded {len} bytes from WSL → LAN");
                }
            }
        }, cancel.Token);

        // 4. LAN → WSL
        Task.Run(() =>
        {
            IPEndPoint remoteEP = null;
            while (!cancel.Token.IsCancellationRequested)
            {
                byte[] data = udp.Receive(ref remoteEP);
                if (data.Length > 0)
                {
                    conn.Send(data);
                    Console.WriteLine($"Forwarded {data.Length} bytes from LAN → WSL");
                }
            }
        }, cancel.Token);

        Console.WriteLine("Press Ctrl+C to exit.");
        Thread.Sleep(Timeout.Infinite);
    }
}
