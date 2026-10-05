using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace WinTop;

/// <summary>Thin wrapper over a PDH query using locale-independent English counter paths.</summary>
sealed class PdhQuery : IDisposable
{
    readonly IntPtr _q;
    readonly Dictionary<string, IntPtr> _counters = new();

    public PdhQuery()
    {
        if (Native.PdhOpenQueryW(null, IntPtr.Zero, out _q) != 0) _q = IntPtr.Zero;
    }

    public bool Add(string key, string path)
    {
        if (_q == IntPtr.Zero) return false;
        if (Native.PdhAddEnglishCounterW(_q, path, IntPtr.Zero, out var c) != 0) return false;
        _counters[key] = c;
        return true;
    }

    public bool Has(string key) => _counters.ContainsKey(key);

    public void Collect()
    {
        if (_q != IntPtr.Zero) Native.PdhCollectQueryData(_q);
    }

    public unsafe Dictionary<string, double> Read(string key, bool noCap = true)
    {
        var result = new Dictionary<string, double>();
        if (!_counters.TryGetValue(key, out var c)) return result;
        uint fmt = Native.PDH_FMT_DOUBLE | (noCap ? Native.PDH_FMT_NOCAP100 : 0);
        uint size = 0;
        uint st = Native.PdhGetFormattedCounterArrayW(c, fmt, ref size, out uint count, IntPtr.Zero);
        if (st != Native.PDH_MORE_DATA || size == 0) return result;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            st = Native.PdhGetFormattedCounterArrayW(c, fmt, ref size, out count, buf);
            if (st != 0) return result;
            // PDH_FMT_COUNTERVALUE_ITEM_W: { LPWSTR name; { DWORD status; double value } } = 24 bytes on x64
            for (int i = 0; i < count; i++)
            {
                byte* item = (byte*)buf + i * 24;
                string name = Marshal.PtrToStringUni(*(IntPtr*)item) ?? "";
                uint status = *(uint*)(item + 8);
                double val = *(double*)(item + 16);
                if (status == 0 || status == 1) result[name] = val;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return result;
    }

    public void Dispose()
    {
        if (_q != IntPtr.Zero) Native.PdhCloseQuery(_q);
    }
}

/// <summary>Per-process CPU/memory via one NtQuerySystemInformation call (no per-process handles needed).</summary>
sealed class ProcessCollector
{
    readonly Dictionary<(int pid, long create), long> _prevCpu = new();
    long _prevTicks;
    IntPtr _buf = IntPtr.Zero;
    int _bufSize = 1 << 20;

    public unsafe List<ProcInfo> Collect(int logical, bool perCore)
    {
        var list = new List<ProcInfo>(400);
        long now = DateTime.UtcNow.Ticks;
        double elapsed = _prevTicks == 0 ? 0 : now - _prevTicks;

        while (true)
        {
            if (_buf == IntPtr.Zero) _buf = Marshal.AllocHGlobal(_bufSize);
            int st = Native.NtQuerySystemInformation(Native.SystemProcessInformation, _buf, _bufSize, out int needed);
            if (st == unchecked((int)0xC0000004)) // STATUS_INFO_LENGTH_MISMATCH
            {
                Marshal.FreeHGlobal(_buf);
                _buf = IntPtr.Zero;
                _bufSize = Math.Max(needed + 65536, _bufSize * 2);
                continue;
            }
            if (st != 0) return list;
            break;
        }

        var seen = new Dictionary<(int, long), long>();
        byte* p = (byte*)_buf;
        while (true)
        {
            uint next = *(uint*)p;
            int threads = (int)*(uint*)(p + 4);
            long privateWs = *(long*)(p + 8);
            long create = *(long*)(p + 32);
            long user = *(long*)(p + 40);
            long kernel = *(long*)(p + 48);
            ushort nameLen = *(ushort*)(p + 56);
            IntPtr namePtr = *(IntPtr*)(p + 64);
            int pid = (int)*(nint*)(p + 80);
            int ppid = (int)*(nint*)(p + 88);
            int handles = (int)*(uint*)(p + 96);

            if (pid != 0)
            {
                string name = pid == 4 ? "System" : (namePtr != IntPtr.Zero ? Marshal.PtrToStringUni(namePtr, nameLen / 2) : "?");
                long cpu = user + kernel;
                var key = (pid, create);
                seen[key] = cpu;
                double pct = 0;
                if (elapsed > 0 && _prevCpu.TryGetValue(key, out var prev))
                {
                    pct = (cpu - prev) / elapsed * 100.0;
                    if (!perCore) pct /= Math.Max(1, logical);
                    if (pct < 0) pct = 0;
                }
                list.Add(new ProcInfo { Pid = pid, ParentPid = ppid, Name = name, CreateTime = create, Cpu = pct, MemBytes = privateWs, Threads = threads, Handles = handles });
            }
            if (next == 0) break;
            p += next;
        }

        _prevCpu.Clear();
        foreach (var kv in seen) _prevCpu[kv.Key] = kv.Value;
        _prevTicks = now;
        return list;
    }
}

/// <summary>Wi-Fi connection info via the native WLAN API.</summary>
sealed class WifiCollector : IDisposable
{
    IntPtr _h;
    bool _unavailable;

    public unsafe WifiInfo? Collect()
    {
        if (_unavailable) return null;
        try
        {
            if (_h == IntPtr.Zero && Native.WlanOpenHandle(2, IntPtr.Zero, out _, out _h) != 0)
            {
                _unavailable = true; // WLAN service not running / no Wi-Fi hardware
                return null;
            }
            if (Native.WlanEnumInterfaces(_h, IntPtr.Zero, out var listPtr) != 0) return null;
            try
            {
                int n = Marshal.ReadInt32(listPtr);
                if (n == 0) return null;
                WifiInfo? first = null;
                for (int i = 0; i < n; i++)
                {
                    IntPtr ii = listPtr + 8 + i * 532;
                    Guid g = Marshal.PtrToStructure<Guid>(ii);
                    string desc = Marshal.PtrToStringUni(ii + 16) ?? "";
                    int state = Marshal.ReadInt32(ii + 16 + 512);
                    var w = new WifiInfo { Interface = desc, Connected = state == 1 };
                    if (w.Connected) QueryConnection(ref g, w);
                    first ??= w;
                    if (w.Connected) return w;
                }
                return first;
            }
            finally { Native.WlanFreeMemory(listPtr); }
        }
        catch (DllNotFoundException) { _unavailable = true; return null; }
        catch { return null; }
    }

    void QueryConnection(ref Guid g, WifiInfo w)
    {
        uint r = Native.WlanQueryInterface(_h, ref g, 7 /* current_connection */, IntPtr.Zero, out _, out var data, out _);
        if (r == 0)
        {
            try
            {
                int ssidLen = Math.Clamp(Marshal.ReadInt32(data + 520), 0, 32);
                var ssid = new byte[ssidLen];
                Marshal.Copy(data + 524, ssid, 0, ssidLen);
                w.Ssid = System.Text.Encoding.UTF8.GetString(ssid);
                int phy = Marshal.ReadInt32(data + 568);
                w.Phy = phy switch
                {
                    4 => "802.11a", 5 => "802.11b", 6 => "802.11g", 7 => "Wi-Fi 4", 8 => "Wi-Fi 5",
                    9 => "802.11ad", 10 => "Wi-Fi 6", 11 => "Wi-Fi 7", _ => "",
                };
                w.SignalPct = Marshal.ReadInt32(data + 576);
                w.RxMbps = (uint)Marshal.ReadInt32(data + 580) / 1000.0;
                w.TxMbps = (uint)Marshal.ReadInt32(data + 584) / 1000.0;
            }
            finally { Native.WlanFreeMemory(data); }
        }
        else if (r == 5)
        {
            w.Error = "location access denied (Settings > Privacy > Location)";
        }

        if (Native.WlanQueryInterface(_h, ref g, 0x10000102 /* rssi */, IntPtr.Zero, out _, out var rssi, out _) == 0)
        {
            w.Rssi = Marshal.ReadInt32(rssi);
            Native.WlanFreeMemory(rssi);
        }
        if (Native.WlanQueryInterface(_h, ref g, 8 /* channel */, IntPtr.Zero, out _, out var ch, out _) == 0)
        {
            w.Channel = Marshal.ReadInt32(ch);
            Native.WlanFreeMemory(ch);
        }
        // Without location permission the signal quality is still derivable from RSSI.
        if (w.SignalPct == null && w.Rssi is int rv) w.SignalPct = Math.Clamp(2 * (rv + 100), 0, 100);
    }

    public void Dispose()
    {
        if (_h != IntPtr.Zero) Native.WlanCloseHandle(_h, IntPtr.Zero);
    }
}

/// <summary>Network adapter throughput.</summary>
sealed class NetCollector
{
    readonly Dictionary<string, (long rx, long tx, long ticks)> _prev = new();

    public List<NetInfo> Collect()
    {
        var list = new List<NetInfo>();
        long now = DateTime.UtcNow.Ticks;
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); } catch { return list; }

        foreach (var n in nics)
        {
            if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (n.OperationalStatus != OperationalStatus.Up) continue;
            try
            {
                var st = n.GetIPStatistics();
                var ipp = n.GetIPProperties();
                var info = new NetInfo
                {
                    Id = n.Id,
                    Name = n.Name,
                    Description = n.Description,
                    Type = n.NetworkInterfaceType switch
                    {
                        NetworkInterfaceType.Wireless80211 => "wifi",
                        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "eth",
                        _ => n.NetworkInterfaceType.ToString().ToLowerInvariant(),
                    },
                    SpeedBps = n.Speed,
                    RxTotal = st.BytesReceived,
                    TxTotal = st.BytesSent,
                    IsDefault = ipp.GatewayAddresses.Any(g => !g.Address.Equals(System.Net.IPAddress.Any) && !g.Address.Equals(System.Net.IPAddress.IPv6Any)),
                    Ip = ipp.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "",
                };
                if (_prev.TryGetValue(n.Id, out var p) && now > p.ticks)
                {
                    double sec = (now - p.ticks) / 1e7;
                    info.RxBps = Math.Max(0, (info.RxTotal - p.rx) / sec);
                    info.TxBps = Math.Max(0, (info.TxTotal - p.tx) / sec);
                }
                _prev[n.Id] = (info.RxTotal, info.TxTotal, now);
                list.Add(info);
            }
            catch { }
        }
        // Default-route adapters first, then by traffic.
        return list.OrderByDescending(x => x.IsDefault).ThenByDescending(x => x.RxTotal + x.TxTotal).ToList();
    }
}
