using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;

namespace WinTop;

/// <summary>Wraps LibreHardwareMonitorLib: temperatures, fans, clocks, power, GPU.</summary>
public sealed class HardwareMonitor : IDisposable
{
    Computer? _computer;
    volatile bool _ready;
    public string Status { get; private set; } = "sensors: initializing...";
    public bool Ready => _ready;

    static readonly Regex CoreNum = new(@"Core #(\d+)", RegexOptions.Compiled);

    public void OpenAsync(HardwareConfig hc)
    {
        Task.Run(() =>
        {
            try
            {
                var c = new Computer
                {
                    IsCpuEnabled = hc.Cpu,
                    IsGpuEnabled = hc.Gpu,
                    IsMotherboardEnabled = hc.Motherboard,
                    IsStorageEnabled = hc.Storage,
                    IsMemoryEnabled = hc.Memory,
                    IsControllerEnabled = hc.Controller,
                    IsPsuEnabled = hc.Psu,
                    IsBatteryEnabled = hc.Battery,
                    IsNetworkEnabled = false,
                };
                c.Open();
                _computer = c;
                Status = "";
                _ready = true;
            }
            catch (Exception ex)
            {
                Status = "sensors unavailable: " + ex.Message;
            }
        });
    }

    public void Update()
    {
        if (!_ready || _computer == null) return;
        foreach (var hw in _computer.Hardware) UpdateRec(hw);
    }

    static void UpdateRec(IHardware hw)
    {
        try { hw.Update(); } catch { }
        foreach (var sub in hw.SubHardware) UpdateRec(sub);
    }

    public IEnumerable<IHardware> AllHardware()
    {
        if (!_ready || _computer == null) yield break;
        var stack = new Stack<IHardware>(_computer.Hardware.Reverse());
        while (stack.Count > 0)
        {
            var h = stack.Pop();
            yield return h;
            foreach (var s in h.SubHardware.Reverse()) stack.Push(s);
        }
    }

    static bool IsGpu(HardwareType t) => t is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel;

    static double? Find(IHardware h, SensorType type, params string[] names)
    {
        foreach (var n in names)
            foreach (var s in h.Sensors)
                if (s.SensorType == type && s.Value.HasValue && string.Equals(s.Name, n, StringComparison.OrdinalIgnoreCase))
                    return s.Value;
        return null;
    }

    static double? FindContains(IHardware h, SensorType type, string part)
    {
        foreach (var s in h.Sensors)
            if (s.SensorType == type && s.Value.HasValue && s.Name.Contains(part, StringComparison.OrdinalIgnoreCase))
                return s.Value;
        return null;
    }

    static string ShortHw(IHardware h) => h.HardwareType switch
    {
        HardwareType.Cpu => "CPU",
        HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel => "GPU",
        HardwareType.Motherboard or HardwareType.SuperIO => "Board",
        HardwareType.Storage => "Disk",
        HardwareType.Memory => "RAM",
        HardwareType.Battery => "Batt",
        HardwareType.Psu => "PSU",
        _ => h.HardwareType.ToString(),
    };

    /// <summary>Fills CPU temps/clocks/power, GPUs, fans, temps into the snapshot.</summary>
    public void Fill(Snapshot s)
    {
        s.HwStatus = Status;
        if (!_ready) return;

        var temps = new List<Reading>();
        var fans = new List<Reading>();
        var powers = new List<Reading>();
        int gpuIdx = 0;

        foreach (var h in AllHardware())
        {
            if (h.HardwareType == HardwareType.Cpu)
            {
                if (string.IsNullOrEmpty(s.CpuName)) s.CpuName = h.Name;
                s.CpuTemp ??= PickCpuTemp(h, out s.CpuTempSource);
                var pkg = Find(h, SensorType.Power, "Package", "CPU Package");
                if (pkg > 0) s.CpuPower ??= pkg; // reads 0 without driver access

                var coreTemps = new SortedDictionary<int, double>();
                var coreClocks = new SortedDictionary<int, double>();
                foreach (var sn in h.Sensors)
                {
                    if (!sn.Value.HasValue) continue;
                    var m = CoreNum.Match(sn.Name);
                    if (sn.SensorType == SensorType.Temperature && m.Success
                        && !sn.Name.Contains("Distance") && !sn.Name.Contains("Max") && !sn.Name.Contains("Average"))
                        coreTemps[int.Parse(m.Groups[1].Value)] = sn.Value.Value;
                    else if (sn.SensorType == SensorType.Clock && m.Success && !sn.Name.Contains("Effective"))
                        coreClocks[int.Parse(m.Groups[1].Value)] = sn.Value.Value;
                }
                if (coreTemps.Count > 0) s.CoreTemps = coreTemps.Values.Select(v => (double?)v).ToArray();
                if (coreClocks.Count > 0) s.CoreClocks = coreClocks.Values.Select(v => (double?)v).ToArray();
            }
            else if (IsGpu(h.HardwareType))
            {
                var g = new GpuInfo
                {
                    Name = h.Name,
                    Load = Find(h, SensorType.Load, "GPU Core", "D3D 3D"),
                    Temp = Find(h, SensorType.Temperature, "GPU Core", "GPU VR SoC", "GPU SoC"),
                    HotSpot = Find(h, SensorType.Temperature, "GPU Hot Spot"),
                    MemTemp = Find(h, SensorType.Temperature, "GPU Memory"),
                    VramUsedMb = Find(h, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used"),
                    VramTotalMb = Find(h, SensorType.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total"),
                    CoreClock = Find(h, SensorType.Clock, "GPU Core"),
                    MemClock = Find(h, SensorType.Clock, "GPU Memory"),
                    Power = Find(h, SensorType.Power, "GPU Package", "GPU Power", "GPU Core") ?? FindContains(h, SensorType.Power, "GPU"),
                    FanRpm = FindContains(h, SensorType.Fan, "Fan"),
                    FanPct = FindContains(h, SensorType.Control, "Fan"),
                };
                // Skip GPUs that report nothing useful (e.g. disabled adapters).
                if (g.Load != null || g.Temp != null || g.VramTotalMb != null)
                    s.Gpus.Add(g);
                gpuIdx++;
            }

            foreach (var sn in h.Sensors)
            {
                if (!sn.Value.HasValue) continue;
                float v = sn.Value.Value;
                switch (sn.SensorType)
                {
                    case SensorType.Temperature:
                        if (sn.Name.Contains("Distance to TjMax") || sn.Name is "Core Max" || sn.Name.Contains("Warning")
                            || sn.Name.Contains("Critical") || sn.Name.Contains("Limit") || float.IsNaN(v) || v <= 0 || v > 150) break;
                        temps.Add(new Reading(GroupName(h, gpuIdx), sn.Name, v));
                        break;
                    case SensorType.Fan:
                        fans.Add(new Reading(GroupName(h, gpuIdx), sn.Name, v));
                        break;
                    case SensorType.Power:
                        powers.Add(new Reading(GroupName(h, gpuIdx), sn.Name, v));
                        break;
                }
            }
        }
        s.Temps = temps;
        s.Fans = fans;
        s.Powers = powers;
    }

    static string GroupName(IHardware h, int gpuIdx)
    {
        if (h.HardwareType == HardwareType.Storage) return Trim(h.Name, 20);
        return ShortHw(h);
    }

    static string Trim(string s, int n) => s.Length <= n ? s : s[..n];

    static double? PickCpuTemp(IHardware h, out string source)
    {
        foreach (var n in new[] { "Core Average", "Core (Tctl/Tdie)", "CPU Package", "Core (Tctl)", "Tdie", "Package", "CPU Cores" })
        {
            var v = Find(h, SensorType.Temperature, n);
            if (v.HasValue && v > 0) { source = n; return v; }
        }
        foreach (var sn in h.Sensors)
            if (sn.SensorType == SensorType.Temperature && sn.Value is > 0 and < 150 && !sn.Name.Contains("Distance"))
            { source = sn.Name; return sn.Value; }
        source = "";
        return null;
    }

    /// <summary>Text dump of every sensor, for --dump diagnostics.</summary>
    public IEnumerable<string> DumpLines()
    {
        foreach (var h in AllHardware())
        {
            yield return $"[{h.HardwareType}] {h.Name}  ({h.Identifier})";
            foreach (var sn in h.Sensors.OrderBy(x => x.SensorType).ThenBy(x => x.Index))
                yield return $"    {sn.SensorType,-12} {sn.Name,-36} {(sn.Value.HasValue ? sn.Value.Value.ToString("0.###") : "null")}";
        }
    }

    public void Dispose()
    {
        try { _computer?.Close(); } catch { }
    }
}
