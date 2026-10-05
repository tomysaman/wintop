using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinTop;

/// <summary>Collects all metrics on a background thread and publishes immutable snapshots.</summary>
public sealed class Sampler : IDisposable
{
    readonly Config _cfg;
    readonly HardwareMonitor _hw = new();
    readonly PdhQuery _pdh = new();
    readonly ProcessCollector _procs = new();
    readonly NetCollector _net = new();
    readonly WifiCollector _wifi = new();
    readonly int _logical = Environment.ProcessorCount;
    readonly int _physical;
    readonly string _cpuName;
    readonly bool _elevated = Native.IsElevated();
    Thread? _thread;
    volatile bool _stop;
    readonly AutoResetEvent _wake = new(false);

    public Snapshot? Latest { get; private set; }
    public event Action? Updated;
    public HardwareMonitor Hardware => _hw;

    public Sampler(Config cfg)
    {
        _cfg = cfg;
        (_physical, _cpuName) = CpuIdentity();
        if (!_pdh.Add("cpu", @"\Processor Information(*)\% Processor Utility"))
            _pdh.Add("cpu", @"\Processor Information(*)\% Processor Time");
        _pdh.Add("dread", @"\LogicalDisk(*)\Disk Read Bytes/sec");
        _pdh.Add("dwrite", @"\LogicalDisk(*)\Disk Write Bytes/sec");
        _pdh.Add("didle", @"\LogicalDisk(*)\% Idle Time");
        _pdh.Add("gpu", @"\GPU Engine(*)\Utilization Percentage");
        _pdh.Add("sb_core", @"\Memory\Standby Cache Core Bytes");
        _pdh.Add("sb_norm", @"\Memory\Standby Cache Normal Priority Bytes");
        _pdh.Add("sb_res", @"\Memory\Standby Cache Reserve Bytes");
        _pdh.Add("modified", @"\Memory\Modified Page List Bytes");
        _pdh.Add("pf", @"\Paging File(_Total)\% Usage");
        _pdh.Collect();
    }

    public void Start()
    {
        _hw.OpenAsync(_cfg.Hardware);
        _thread = new Thread(Loop) { IsBackground = true, Name = "sampler" };
        _thread.Start();
    }

    /// <summary>Takes one sample immediately (used by --dump).</summary>
    public Snapshot SampleNow() => Sample();

    void Loop()
    {
        // Short first interval so the UI fills quickly.
        _wake.WaitOne(300);
        while (!_stop)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                Latest = Sample();
                Updated?.Invoke();
            }
            catch { }
            int wait = Math.Max(50, _cfg.UpdateMs - (int)sw.ElapsedMilliseconds);
            _wake.WaitOne(wait);
        }
    }

    Snapshot Sample()
    {
        var sw = Stopwatch.StartNew();
        var s = new Snapshot { Elevated = _elevated, LogicalCores = _logical, PhysicalCores = _physical, CpuName = _cpuName };

        _pdh.Collect();
        SampleCpu(s);
        SampleMemory(s);
        SampleCacheAndSwap(s);
        SampleDisks(s);
        Try(() => s.Nets = _net.Collect());
        Try(() => s.Wifi = _wifi.Collect());
        Try(() => s.Procs = _procs.Collect(_logical, _cfg.ProcPerCore));
        Try(() => OwnerResolver.Annotate(s.Procs));
        SampleSystem(s);
        if (_cfg.Boxes.Contains("ports"))
            Try(() =>
            {
                var names = new Dictionary<int, string>();
                var apps = new Dictionary<int, string?>();
                foreach (var p in s.Procs) { names[p.Pid] = p.Name; apps[p.Pid] = p.App; }
                (s.Ports, s.TcpEstablished) = PortCollector.Collect(names);
                foreach (var pt in s.Ports) pt.App = apps.GetValueOrDefault(pt.Pid);
            });

        Try(() => { _hw.Update(); _hw.Fill(s); });
        if (!_hw.Ready) s.HwStatus = _hw.Status;
        var engines = _pdh.Read("gpu");
        Try(() => SampleGpuPerProcess(s, engines));
        if (s.Gpus.Count == 0) SampleGpuFallback(s, engines);
        s.CpuName = CleanCpuName(s.CpuName);
        s.SampleMs = sw.Elapsed.TotalMilliseconds;
        return s;
    }

    static void Try(Action a)
    {
        try { a(); } catch { }
    }

    void SampleCpu(Snapshot s)
    {
        var v = _pdh.Read("cpu", noCap: false);
        var threads = new SortedDictionary<(int, int), double>();
        foreach (var (name, val) in v)
        {
            if (name == "_Total") { s.CpuLoad = Math.Clamp(val, 0, 100); continue; }
            var parts = name.Split(',');
            if (parts.Length == 2 && int.TryParse(parts[0], out int g) && int.TryParse(parts[1], out int i))
                threads[(g, i)] = Math.Clamp(val, 0, 100);
        }
        s.ThreadLoads = threads.Values.ToArray();
        if (!v.ContainsKey("_Total") && s.ThreadLoads.Length > 0) s.CpuLoad = s.ThreadLoads.Average();
    }

    static void SampleMemory(Snapshot s)
    {
        var ms = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (Native.GlobalMemoryStatusEx(ref ms))
        {
            s.MemTotal = (long)ms.ullTotalPhys;
            s.MemAvailable = (long)ms.ullAvailPhys;
            s.MemUsed = s.MemTotal - s.MemAvailable;
        }
        if (Native.GetPerformanceInfo(out var pi, (uint)Marshal.SizeOf<Native.PERFORMANCE_INFORMATION>()))
        {
            long page = (long)pi.PageSize;
            s.MemCached = (long)pi.SystemCache * page;
            s.CommitTotal = (long)pi.CommitTotal * page;
            s.CommitLimit = (long)pi.CommitLimit * page;
            s.ProcessCount = (int)pi.ProcessCount;
            s.ThreadCount = (int)pi.ThreadCount;
            s.HandleCount = (int)pi.HandleCount;
            // Page file size = commit limit - physical RAM; used portion approximated from commit charge beyond RAM.
            s.SwapTotal = Math.Max(0, s.CommitLimit - s.MemTotal);
            s.SwapUsed = Math.Clamp(s.CommitTotal - s.MemUsed, 0, s.SwapTotal);
        }
    }

    void SampleCacheAndSwap(Snapshot s)
    {
        // Task Manager's "Cached" = standby lists + modified page list.
        double cached = 0;
        bool any = false;
        foreach (var k in new[] { "sb_core", "sb_norm", "sb_res", "modified" })
            foreach (var v in _pdh.Read(k).Values) { cached += v; any = true; }
        if (any) s.MemCached = (long)cached;
        var pf = _pdh.Read("pf");
        if (pf.Count > 0 && s.SwapTotal > 0) s.SwapUsed = (long)(s.SwapTotal * Math.Clamp(pf.Values.First(), 0, 100) / 100);
    }

    void SampleDisks(Snapshot s)
    {
        var rd = _pdh.Read("dread");
        var wr = _pdh.Read("dwrite");
        var idle = _pdh.Read("didle");
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable) || !d.IsReady) continue;
                string letter = d.Name.TrimEnd('\\');
                if (_cfg.DiskHide.Any(h => string.Equals(h.TrimEnd('\\'), letter, StringComparison.OrdinalIgnoreCase))) continue;
                var di = new DiskInfo
                {
                    Letter = letter,
                    Label = d.VolumeLabel,
                    Format = d.DriveFormat,
                    Kind = d.DriveType == DriveType.Removable ? "usb" : "",
                    Total = d.TotalSize,
                    Free = d.TotalFreeSpace,
                };
                if (rd.TryGetValue(letter, out var r)) di.ReadBps = r;
                if (wr.TryGetValue(letter, out var w)) di.WriteBps = w;
                if (idle.TryGetValue(letter, out var i)) di.ActivePct = Math.Clamp(100 - i, 0, 100);
                s.Disks.Add(di);
            }
            catch { }
        }
    }

    /// <summary>
    /// Per-process GPU %, computed like Task Manager: for each process and adapter, sum the
    /// utilization of all engines of the same type (3D, Copy, VideoDecode...), then take the busiest type.
    /// Instance names look like "pid_1234_luid_0x0_0xD1A3_phys_0_eng_0_engtype_3D".
    /// </summary>
    static void SampleGpuPerProcess(Snapshot s, Dictionary<string, double> v)
    {
        if (v.Count == 0 || s.Procs.Count == 0) return;
        var sums = new Dictionary<(int pid, string luid, string type), double>();
        foreach (var (name, val) in v)
        {
            if (val <= 0 || !name.StartsWith("pid_", StringComparison.Ordinal)) continue;
            int us = name.IndexOf('_', 4);
            int li = name.IndexOf("luid_", StringComparison.Ordinal);
            int ti = name.IndexOf("engtype_", StringComparison.Ordinal);
            if (us < 0 || li < 0 || ti < 0 || !int.TryParse(name.AsSpan(4, us - 4), out int pid)) continue;
            int pe = name.IndexOf("_phys", li, StringComparison.Ordinal);
            var key = (pid, pe > li ? name[li..pe] : "", name[(ti + 8)..]);
            sums[key] = sums.GetValueOrDefault(key) + val;
        }
        var perPid = new Dictionary<int, double>();
        foreach (var (k, val) in sums)
            perPid[k.pid] = Math.Max(perPid.GetValueOrDefault(k.pid), val);
        foreach (var p in s.Procs)
            if (perPid.TryGetValue(p.Pid, out var g)) p.Gpu = Math.Clamp(g, 0, 100);
    }

    static void SampleGpuFallback(Snapshot s, Dictionary<string, double> v)
    {
        // Task-Manager style: per adapter (luid), sum engines of each type, take the busiest type.
        if (v.Count == 0) return;
        var perLuid = new Dictionary<string, Dictionary<string, double>>();
        foreach (var (name, val) in v)
        {
            int li = name.IndexOf("luid_", StringComparison.Ordinal);
            int ti = name.IndexOf("engtype_", StringComparison.Ordinal);
            if (li < 0 || ti < 0) continue;
            string luid = name.Substring(li, Math.Min(26, name.Length - li));
            string type = name[(ti + 8)..];
            if (!perLuid.TryGetValue(luid, out var t)) perLuid[luid] = t = new();
            t[type] = t.GetValueOrDefault(type) + val;
        }
        int n = 0;
        foreach (var (_, types) in perLuid.OrderByDescending(kv => kv.Value.Values.Max()))
        {
            double load = Math.Clamp(types.Values.Max(), 0, 100);
            if (n > 0 && load <= 0) continue;
            s.Gpus.Add(new GpuInfo { Name = $"GPU {n}", Load = load });
            n++;
        }
    }

    void SampleSystem(Snapshot s)
    {
        s.Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        if (Native.GetSystemPowerStatus(out var ps))
        {
            if (ps.BatteryFlag != 128 && ps.BatteryFlag != 255 && ps.BatteryLifePercent <= 100)
                s.BatteryPct = ps.BatteryLifePercent;
            if (ps.ACLineStatus <= 1) s.OnAc = ps.ACLineStatus == 1;
        }
    }

    static (int physical, string name) CpuIdentity()
    {
        int physical = 0;
        string name = "";
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            name = key?.GetValue("ProcessorNameString") as string ?? "";
        }
        catch { }
        try
        {
            // Count RelationProcessorCore entries.
            uint len = 0;
            GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref len);
            IntPtr buf = Marshal.AllocHGlobal((int)len);
            try
            {
                if (GetLogicalProcessorInformationEx(0, buf, ref len))
                {
                    int off = 0;
                    while (off < len)
                    {
                        physical++;
                        off += Marshal.ReadInt32(buf + off + 4);
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { }
        if (physical == 0) physical = Environment.ProcessorCount;
        return (physical, name.Trim());
    }

    static string CleanCpuName(string n)
    {
        foreach (var junk in new[] { "(R)", "(TM)", "(tm)", " CPU", " Processor", "with Radeon Graphics", "with Radeon Vega Graphics" })
            n = n.Replace(junk, "");
        int at = n.IndexOf(" @ ", StringComparison.Ordinal);
        if (at > 0) n = n[..at];
        return System.Text.RegularExpressions.Regex.Replace(n, @"\s+", " ").Trim();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint returnedLength);

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread?.Join(2000);
        _hw.Dispose();
        _pdh.Dispose();
        _wifi.Dispose();
    }
}
