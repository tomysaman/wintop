namespace WinTop;

public sealed class GpuInfo
{
    public string Name = "";
    public double? Load;
    public double? Temp;
    public double? HotSpot;
    public double? MemTemp;
    public double? VramUsedMb;
    public double? VramTotalMb;
    public double? CoreClock;
    public double? MemClock;
    public double? Power;
    public double? FanRpm;
    public double? FanPct;
}

public sealed class DiskInfo
{
    public string Letter = "";
    public string Label = "";
    public string Format = "";
    public string Kind = "";
    public long Total;
    public long Free;
    public double ReadBps;
    public double WriteBps;
    public double? ActivePct;
    public double UsedPct => Total > 0 ? 100.0 * (Total - Free) / Total : 0;
}

public sealed class NetInfo
{
    public string Id = "";
    public string Name = "";
    public string Description = "";
    public string Type = "";
    public string Ip = "";
    public bool IsDefault;
    public long SpeedBps;
    public double RxBps;
    public double TxBps;
    public long RxTotal;
    public long TxTotal;
}

public sealed class WifiInfo
{
    public string Interface = "";
    public string Ssid = "";
    public int? SignalPct;
    public int? Rssi;
    public double? RxMbps;
    public double? TxMbps;
    public string Phy = "";
    public int? Channel;
    public string? Error;
    public bool Connected;
}

public sealed record Reading(string Group, string Name, double Value);

public sealed class ProcInfo
{
    public int Pid;
    public int ParentPid;
    public string Name = "";
    public string? App;     // owning application of a generic host (node.exe -> "Raycast")
    public long CreateTime;
    public double Cpu;
    public double Gpu;
    public long MemBytes;
    public int Threads;
    public int Handles;
}

public sealed class Snapshot
{
    public DateTime Time = DateTime.Now;

    // CPU
    public string CpuName = "";
    public int LogicalCores;
    public int PhysicalCores;
    public double CpuLoad;
    public double[] ThreadLoads = Array.Empty<double>();
    public double? CpuTemp;
    public string CpuTempSource = "";
    public double?[] CoreTemps = Array.Empty<double?>();
    public double?[] CoreClocks = Array.Empty<double?>();
    public double? CpuPower;

    // Memory
    public long MemTotal, MemUsed, MemAvailable, MemCached;
    public long CommitTotal, CommitLimit;
    public long SwapTotal, SwapUsed;

    public List<GpuInfo> Gpus = new();
    public List<DiskInfo> Disks = new();
    public List<NetInfo> Nets = new();
    public WifiInfo? Wifi;
    public List<Reading> Fans = new();
    public List<Reading> Temps = new();
    public List<Reading> Powers = new();
    public List<ProcInfo> Procs = new();
    public List<PortInfo> Ports = new();
    public int TcpEstablished;

    // System
    public TimeSpan Uptime;
    public int ProcessCount, ThreadCount, HandleCount;
    public int? BatteryPct;
    public bool? OnAc;
    public bool Elevated;
    public string HwStatus = "";
    public double SampleMs;
}
