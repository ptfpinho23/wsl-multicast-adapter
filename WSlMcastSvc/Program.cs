using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Hyper-V AF_HYPERV
public enum HvAddressFamily : int { AF_HYPERV = 34 }

[StructLayout(LayoutKind.Sequential)]
public struct SOCKADDR_HV
{
    public ushort Family;     // AF_HYPERV
    public ushort Reserved;   // must be 0
    public Guid VmId;         // target VM (Guid.Empty == host)
    public Guid ServiceId;    // GUID derived from VSOCK port
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
        var sa = new SocketAddress(AddressFamily, 36);

        ushort fam = (ushort)AddressFamily;
        sa[0] = (byte)(fam & 0xFF);
        sa[1] = (byte)((fam >> 8) & 0xFF);

        sa[2] = 0; // reserved
        sa[3] = 0;

        var vm = VmId.ToByteArray();
        var svc = ServiceId.ToByteArray();
        for (int i = 0; i < 16; i++) sa[4 + i] = vm[i];
        for (int i = 0; i < 16; i++) sa[20 + i] = svc[i];

        return sa;
    }

    public override EndPoint Create(SocketAddress sa)
    {
        if (sa.Family != AddressFamily || sa.Size < 36)
            throw new ArgumentException("Invalid Hyper-V socket address.");

        var vm = new byte[16];
        var svc = new byte[16];
        for (int i = 0; i < 16; i++) vm[i] = sa[4 + i];
        for (int i = 0; i < 16; i++) svc[i] = sa[20 + i];
        return new HvEndPoint(new Guid(vm), new Guid(svc));
    }

    public override string ToString() => $"hvsock:{VmId}:{ServiceId}";
}

class WslMcastSvc
{
    const int BUFFER_SIZE = 2048;

    // WSAStartup / WSACleanup interop
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

    [DllImport("Ws2_32.dll", SetLastError = true)]
    private static extern IntPtr socket(int af, int type, int protocol);

    static Guid GuidForPort(ushort port)
    {
        // Replace Data1 with port (hex, 8 digits)
        string hex = port.ToString("x8");
        return Guid.Parse($"{hex}-facb-11e6-bd58-64006a7986d3");
    }

    static void Main()
    {
        const ushort PORT = 5000; // must match Linux svm_port
        var serviceId = GuidForPort(PORT);
        Console.WriteLine($"Service GUID for port {PORT}: {serviceId}");

        // Initialize Winsock (request version 2.2)
        WSAData d;
        int res = WSAStartup(0x202, out d);
        if (res != 0)
            throw new SocketException(res);

        try
        {
            // Create AF_HYPERV socket
            IntPtr raw = socket((int)HvAddressFamily.AF_HYPERV, (int)SocketType.Stream, 1);
            if (raw == IntPtr.Zero || raw.ToInt64() == -1)
                throw new SocketException(Marshal.GetLastWin32Error());

            using var listen = new Socket(new SafeSocketHandle(raw, ownsHandle: true));

            // Bind on host (VmId = Guid.Empty) and Service GUID
            listen.Bind(new HvEndPoint(Guid.Empty, serviceId));
            listen.Listen(1);

            Console.WriteLine("Waiting for WSL connection...");
            using var conn = listen.Accept();
            Console.WriteLine("Connected.");

            var buf = new byte[BUFFER_SIZE];
            while (true)
            {
                int n = conn.Receive(buf);
                if (n <= 0) break;

                if (n >= 2)
                {
                    ushort framelen = BitConverter.ToUInt16(buf, 0);
                    Console.WriteLine($"Got frame {framelen} bytes");

                    // optional echo back
                    conn.Send(buf, 0, 2 + framelen, SocketFlags.None);
                }
            }
        }
        finally
        {
            WSACleanup();
        }
    }
}
