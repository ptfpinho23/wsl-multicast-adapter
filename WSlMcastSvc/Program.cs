using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

// .NET doesn't expose AF_VSOCK directly.
public enum VsockAddressFamily : ushort
{
    AF_VSOCK = 40
}

public static class Vsock
{
    public const uint CID_ANY  = 0xFFFFFFFF; // wildcard listen
    public const uint CID_HOST = 2;          // host (Windows) CID
}

// Raw sockaddr_vm is 16 bytes on Windows/Linux for AF_VSOCK.
public sealed class VsockEndPoint : EndPoint
{
    public uint Cid  { get; }
    public uint Port { get; }

    public VsockEndPoint(uint cid, uint port)
    {
        Cid = cid;
        Port = port;
    }

    public override AddressFamily AddressFamily => (AddressFamily)VsockAddressFamily.AF_VSOCK;

    public override SocketAddress Serialize()
    {
        // sockaddr_vm (16 bytes):
        // 0..1  : family (AF_VSOCK)
        // 2..3  : reserved (0)
        // 4..7  : CID
        // 8..11 : Port
        // 12..15: reserved (0)
        var sa = new SocketAddress(AddressFamily, 16);

        ushort fam = (ushort)AddressFamily;
        sa[0] = (byte)(fam & 0xFF);      // low byte
        sa[1] = (byte)((fam >> 8) & 0xFF); // high byte

        byte[] cidBytes  = BitConverter.GetBytes(Cid);
        byte[] portBytes = BitConverter.GetBytes(Port);
        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(cidBytes);
            Array.Reverse(portBytes);
        }
        for (int i = 0; i < 4; i++) sa[4 + i]  = cidBytes[i];
        for (int i = 0; i < 4; i++) sa[8 + i]  = portBytes[i];
        // bytes 2-3 and 12-15 remain zero

        return sa;
    }

    public override EndPoint Create(SocketAddress socketAddress)
    {
        if (socketAddress.Family != AddressFamily || socketAddress.Size < 16)
            throw new ArgumentException("Invalid VSOCK socket address.");

       // sockaddr_vm (16 bytes):
        // 0..1  : family (AF_VSOCK)
        // 2..3  : reserved (0)
        // 4..7  : CID
        // 8..11 : Port
        // 12..15: reserved (0)
        byte[] cidBytes  = new byte[4];
        byte[] portBytes = new byte[4];
        for (int i = 0; i < 4; i++) cidBytes[i]  = socketAddress[4 + i];
        for (int i = 0; i < 4; i++) portBytes[i] = socketAddress[8 + i];
        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(cidBytes);
            Array.Reverse(portBytes);
        }
        uint cid  = BitConverter.ToUInt32(cidBytes, 0);
        uint port = BitConverter.ToUInt32(portBytes, 0);

        return new VsockEndPoint(cid, port);
    }

    public override string ToString() => $"vsock:{Cid}:{Port}";
}

class WslMcastSvc
{
    const int VSOCK_PORT   = 12345;            // vsock svc port 
    const int UDP_PORT     = 5000;             // multicast UDP port used 
    const string MCAST_GRP = "224.1.1.1";      // group choosen for testing
    const int BUFFER_SIZE  = 2048;

    static void Main(string[] args)
    {
        Console.CancelKeyPress += (s, e) => { e.Cancel = true; _cts.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (s, e) => _cts.Cancel();

        Console.WriteLine("Starting WslMcastSvc...");

        // vsock listener
        using var vsockListen = new Socket((AddressFamily)VsockAddressFamily.AF_VSOCK,
                                           SocketType.Stream,
                                           ProtocolType.Unspecified);

        vsockListen.Bind(new VsockEndPoint(Vsock.CID_ANY, (uint)VSOCK_PORT));
        vsockListen.Listen(1);

        Console.WriteLine($"Waiting for vsock connection on port {VSOCK_PORT}...");
        using var vsockConn = vsockListen.Accept();
        Console.WriteLine("Connected to WSL peer.");

        // 2) UDP multicast socket (join group)
        using var udp = new UdpClient(UDP_PORT);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.JoinMulticastGroup(IPAddress.Parse(MCAST_GRP));

        Console.WriteLine($"Joined multicast group {MCAST_GRP}:{UDP_PORT}");

        // Kick off two way tasks
        var t1 = Task.Run(() => VsockToLan(vsockConn, udp, _cts.Token));
        var t2 = Task.Run(() => LanToVsock(udp, vsockConn, _cts.Token));

        Console.WriteLine("Press Ctrl+C to stop...");
        try { Task.WaitAll(new[] { t1, t2 }); } catch (AggregateException) { /* ignore on cancel */ }
        Console.WriteLine("Shutting down.");
    }

    static readonly CancellationTokenSource _cts = new CancellationTokenSource();

    static async Task VsockToLan(Socket vsockConn, UdpClient udp, CancellationToken ct)
    {
        var buf = new byte[BUFFER_SIZE];
        var mcastEndPoint = new IPEndPoint(IPAddress.Parse(MCAST_GRP), UDP_PORT);

        while (!ct.IsCancellationRequested)
        {
            int n;
            try { n = await vsockConn.ReceiveAsync(buf, SocketFlags.None, ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Console.WriteLine($"WSL→LAN recv error: {ex.Message}"); break; }

            if (n > 0)
            {
                try { await udp.SendAsync(new ReadOnlyMemory<byte>(buf, 0, n), mcastEndPoint, ct); }
                catch (Exception ex) { Console.WriteLine($"WSL→LAN send error: {ex.Message}"); break; }
                // Console.WriteLine($"WSL → LAN {n} bytes");
            }
        }
    }

    static async Task LanToVsock(UdpClient udp, Socket vsockConn, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult res;
            try { res = await udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Console.WriteLine($"LAN→WSL recv error: {ex.Message}"); break; }

            var data = res.Buffer;
            if (data != null && data.Length > 0)
            {
                try { await vsockConn.SendAsync(data, SocketFlags.None, ct); }
                catch (Exception ex) { Console.WriteLine($"LAN→WSL send error: {ex.Message}"); break; }
                Console.WriteLine($"LAN → WSL {data.Length} bytes");
            }
        }
    }
}
