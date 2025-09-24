using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

// Hyper-V sockets on Windows
public enum HvAddressFamily : int
{
    AF_HYPERV = 34
}

[StructLayout(LayoutKind.Sequential)]
public struct SOCKADDR_HV
{
    public ushort Family;     // AF_HYPERV
    public ushort Reserved;   // must be 0
    public Guid VmId;         // VM target (GUID_NULL = host)
    public Guid ServiceId;    // service ID (like port# in vsock)
}

public sealed class HvEndPoint : EndPoint
{
    public Guid VmId { get; }
    public Guid ServiceId { get; }

    public HvEndPoint(Guid vmId, Guid serviceId)
    {
        VmId = vmId;
        ServiceId = serviceId;
    }

    public override AddressFamily AddressFamily => (AddressFamily)HvAddressFamily.AF_HYPERV;

    public override SocketAddress Serialize()
    {
        // SOCKADDR_HV = 36 bytes: ushort + ushort + GUID + GUID
        var sa = new SocketAddress(AddressFamily, 36);

        ushort fam = (ushort)AddressFamily;

        // little endian
        sa[0] = (byte)(fam & 0xFF);
        sa[1] = (byte)((fam >> 8) & 0xFF);

        // reserved 
        sa[2] = 0;
        sa[3] = 0;

        byte[] vm = VmId.ToByteArray();
        byte[] svc = ServiceId.ToByteArray();

        // copy vmId
        for (int i = 0; i < 16; i++) sa[4 + i] = vm[i];

        // copy serviceId
        for (int i = 0; i < 16; i++) sa[20 + i] = svc[i];

        return sa;
    }

    public override EndPoint Create(SocketAddress socketAddress)
    {
        if (socketAddress.Family != AddressFamily || socketAddress.Size < 36)
            throw new ArgumentException("Invalid Hyper-V socket address.");

        byte[] vm = new byte[16];
        byte[] svc = new byte[16];
        for (int i = 0; i < 16; i++) vm[i]  = socketAddress[4 + i];
        for (int i = 0; i < 16; i++) svc[i] = socketAddress[20 + i];

        return new HvEndPoint(new Guid(vm), new Guid(svc));
    }

    public override string ToString() => $"hvsock:{VmId}:{ServiceId}";
}

class WslMcastSvc
{
    static readonly CancellationTokenSource _cts = new();

    // GUID service id - for testing
    static readonly Guid SERVICE_ID = Guid.Parse("11111111-2222-3333-4444-555555555555");

    const int UDP_PORT     = 5000;
    const string MCAST_GRP = "224.1.1.1";
    const int BUFFER_SIZE  = 2048;

    // P/Invoke to Winsock
    [DllImport("Ws2_32.dll", SetLastError = true)]
    private static extern IntPtr socket(int af, int type, int protocol);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct WSAData
    {
        public short wVersion;
        public short wHighVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)]
        public string szDescription;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)]
        public string szSystemStatus;
        public short iMaxSockets;
        public short iMaxUdpDg;
        public IntPtr lpVendorInfo;
    }

    [DllImport("Ws2_32.dll", SetLastError = true)]
    private static extern int WSAStartup(ushort wVersionRequested, out WSAData data);

    [DllImport("Ws2_32.dll", SetLastError = true)]
    private static extern int WSACleanup();

    static void Main(string[] args)
    {
        Console.CancelKeyPress += (s, e) => { e.Cancel = true; _cts.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (s, e) => _cts.Cancel();

        Console.WriteLine("Starting Hyper-V multicast bridge...");

        // Initialize Winsock (request version 2.2)
        WSAData d;
        int res = WSAStartup(0x202, out d); // high byte - major; low byte - minor
        if (res != 0)
            throw new SocketException(res);

        try
        {
            // Create native AF_HYPERV socket via Winsock and wrap it in a SafeSocketHandle - FIXME
            IntPtr raw = socket((int)HvAddressFamily.AF_HYPERV, (int)SocketType.Stream, 1);
            if (raw == IntPtr.Zero || raw.ToInt64() == -1)
                throw new SocketException(Marshal.GetLastWin32Error());

            using var hvListen = new Socket(new SafeSocketHandle(raw, ownsHandle: true));

            // Bind/listen using HvEndPoint (GUID_NULL == host)
            hvListen.Bind(new HvEndPoint(Guid.Empty, SERVICE_ID));
            hvListen.Listen(1);

            Console.WriteLine($"Waiting for Hyper-V connection (service {SERVICE_ID})...");
            using var hvConn = hvListen.Accept();
            Console.WriteLine("Connected to WSL peer via Hyper-V socket.");

            // UDP multicast
            using var udp = new UdpClient(UDP_PORT);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.JoinMulticastGroup(IPAddress.Parse(MCAST_GRP));

            Console.WriteLine($"Joined multicast group {MCAST_GRP}:{UDP_PORT}");

            var t1 = Task.Run(() => HvToLan(hvConn, udp, _cts.Token));
            var t2 = Task.Run(() => LanToHv(udp, hvConn, _cts.Token));

            Console.WriteLine("Press Ctrl+C to stop...");
            try { Task.WaitAll(new[] { t1, t2 }); } catch (AggregateException) { }
            Console.WriteLine("Shutting down.");
        }
        finally
        {
            WSACleanup();
        }
    }

    static async Task HvToLan(Socket hvConn, UdpClient udp, CancellationToken ct)
    {
        var buf = new byte[BUFFER_SIZE];
        var mcastEndPoint = new IPEndPoint(IPAddress.Parse(MCAST_GRP), UDP_PORT);

        while (!ct.IsCancellationRequested)
        {
            int n;
            try { n = await hvConn.ReceiveAsync(buf, SocketFlags.None, ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Console.WriteLine($"WSL→LAN recv error: {ex.Message}"); break; }

            if (n > 0)
            {
                try { await udp.SendAsync(new ReadOnlyMemory<byte>(buf, 0, n), mcastEndPoint, ct); }
                catch (Exception ex) { Console.WriteLine($"WSL→LAN send error: {ex.Message}"); break; }
            }
        }
    }

    static async Task LanToHv(UdpClient udp, Socket hvConn, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult res;
            try { res = await udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Console.WriteLine($"LAN -> WSL recv error: {ex.Message}"); break; }

            var data = res.Buffer;
            if (data != null && data.Length > 0)
            {
                try { await hvConn.SendAsync(data, SocketFlags.None, ct); }
                catch (Exception ex) { Console.WriteLine($"LAN -> WSL send error: {ex.Message}"); break; }
                Console.WriteLine($"LAN -> WSL {data.Length} bytes");
            }
        }
    }
}
