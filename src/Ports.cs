using System.Net;
using System.Runtime.InteropServices;

namespace WinTop;

public sealed class PortInfo
{
    public string Proto = "";   // TCP / UDP
    public int Port;
    public string Address = ""; // "all", "local", or an IP
    public int Pid;
    public string Process = "";
    public string? App;
}

/// <summary>Listening TCP ports and bound UDP ports with their owning process (iphlpapi, no admin needed).</summary>
static class PortCollector
{
    const int AF_INET = 2, AF_INET6 = 23;
    const int TCP_TABLE_OWNER_PID_LISTENER = 3, TCP_TABLE_OWNER_PID_CONNECTIONS = 4;
    const int UDP_TABLE_OWNER_PID = 1;
    const int MIB_TCP_STATE_ESTAB = 5;

    [DllImport("iphlpapi.dll")]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll")]
    static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    static int NetPort(uint p) => (int)(((p & 0xFF) << 8) | ((p >> 8) & 0xFF));

    delegate uint TableCall(IntPtr buffer, ref int size);

    /// <summary>Calls one of the Get*Table functions (growing the buffer as asked) and hands each row to the reader.</summary>
    static void ReadTable(TableCall call, int rowSize, Action<IntPtr> row)
    {
        int size = 64 * 1024;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                int want = size;
                uint rc = call(buf, ref want);
                if (rc == 122) { size = Math.Max(want, size) + 16 * 1024; continue; } // ERROR_INSUFFICIENT_BUFFER
                if (rc != 0) return;
                int n = Marshal.ReadInt32(buf);
                for (int i = 0; i < n; i++) row(buf + 4 + i * rowSize);
                return;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }

    static TableCall Tcp(int af, int cls) => (IntPtr b, ref int s) => GetExtendedTcpTable(b, ref s, true, af, cls, 0);
    static TableCall Udp(int af) => (IntPtr b, ref int s) => GetExtendedUdpTable(b, ref s, true, af, UDP_TABLE_OWNER_PID, 0);

    static string Addr4(uint a) => a == 0 ? "all" : (a & 0xFF) == 127 ? "local" : new IPAddress(a).ToString();

    static string Addr6(IntPtr p)
    {
        var b = new byte[16];
        Marshal.Copy(p, b, 0, 16);
        var ip = new IPAddress(b);
        return ip.Equals(IPAddress.IPv6Any) ? "all" : IPAddress.IsLoopback(ip) ? "local" : ip.ToString();
    }

    public static (List<PortInfo> ports, int established) Collect(IReadOnlyDictionary<int, string> names)
    {
        var raw = new List<PortInfo>();
        int est = 0;

        // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid (24 bytes)
        ReadTable(Tcp(AF_INET, TCP_TABLE_OWNER_PID_LISTENER), 24, r =>
            raw.Add(new PortInfo { Proto = "TCP", Address = Addr4((uint)Marshal.ReadInt32(r + 4)), Port = NetPort((uint)Marshal.ReadInt32(r + 8)), Pid = Marshal.ReadInt32(r + 20) }));
        // MIB_TCP6ROW_OWNER_PID: localAddr[16], scope, localPort, remoteAddr[16], scope, remotePort, state, pid (56 bytes)
        ReadTable(Tcp(AF_INET6, TCP_TABLE_OWNER_PID_LISTENER), 56, r =>
            raw.Add(new PortInfo { Proto = "TCP", Address = Addr6(r), Port = NetPort((uint)Marshal.ReadInt32(r + 20)), Pid = Marshal.ReadInt32(r + 52) }));
        // MIB_UDPROW_OWNER_PID: localAddr, localPort, pid (12 bytes)
        ReadTable(Udp(AF_INET), 12, r =>
            raw.Add(new PortInfo { Proto = "UDP", Address = Addr4((uint)Marshal.ReadInt32(r)), Port = NetPort((uint)Marshal.ReadInt32(r + 4)), Pid = Marshal.ReadInt32(r + 8) }));
        // MIB_UDP6ROW_OWNER_PID: localAddr[16], scope, localPort, pid (28 bytes)
        ReadTable(Udp(AF_INET6), 28, r =>
            raw.Add(new PortInfo { Proto = "UDP", Address = Addr6(r), Port = NetPort((uint)Marshal.ReadInt32(r + 20)), Pid = Marshal.ReadInt32(r + 24) }));

        ReadTable(Tcp(AF_INET, TCP_TABLE_OWNER_PID_CONNECTIONS), 24, r => { if (Marshal.ReadInt32(r) == MIB_TCP_STATE_ESTAB) est++; });
        ReadTable(Tcp(AF_INET6, TCP_TABLE_OWNER_PID_CONNECTIONS), 56, r => { if (Marshal.ReadInt32(r + 48) == MIB_TCP_STATE_ESTAB) est++; });

        // One row per (proto, port, pid); IPv4 + IPv6 binds of the same socket merge.
        var merged = raw
            .GroupBy(p => (p.Proto, p.Port, p.Pid))
            .Select(g =>
            {
                var addrs = g.Select(p => p.Address).Distinct().ToList();
                string addr = addrs.Contains("all") ? "all" : addrs.All(a => a == "local") ? "local" : string.Join(",", addrs.Where(a => a != "local"));
                return new PortInfo
                {
                    Proto = g.Key.Proto, Port = g.Key.Port, Pid = g.Key.Pid, Address = addr,
                    Process = g.Key.Pid == 0 ? "Idle" : g.Key.Pid == 4 ? "System" : names.TryGetValue(g.Key.Pid, out var n) ? n : "?",
                };
            })
            .OrderBy(p => p.Proto == "TCP" ? 0 : 1).ThenBy(p => p.Port).ThenBy(p => p.Pid)
            .ToList();
        return (merged, est);
    }
}
