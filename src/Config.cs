using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinTop;

public sealed class HardwareConfig
{
    [JsonPropertyName("cpu")] public bool Cpu { get; set; } = true;
    [JsonPropertyName("gpu")] public bool Gpu { get; set; } = true;
    [JsonPropertyName("motherboard")] public bool Motherboard { get; set; } = true;
    [JsonPropertyName("storage")] public bool Storage { get; set; } = true;
    [JsonPropertyName("memory")] public bool Memory { get; set; } = true;
    [JsonPropertyName("controller")] public bool Controller { get; set; } = true;
    [JsonPropertyName("psu")] public bool Psu { get; set; } = true;
    [JsonPropertyName("battery")] public bool Battery { get; set; } = true;
}

public sealed class Config
{
    /// <summary>Theme name: file theme\&lt;name&gt;.theme or folder theme\&lt;name&gt;\.</summary>
    [JsonPropertyName("theme")] public string Theme { get; set; } = "default";

    /// <summary>Sampling/redraw interval in milliseconds (250..10000).</summary>
    [JsonPropertyName("update_ms")] public int UpdateMs { get; set; } = 1000;

    /// <summary>"braille", "block" or "tty".</summary>
    [JsonPropertyName("graph_symbol")] public string GraphSymbol { get; set; } = "braille";

    /// <summary>"C" or "F".</summary>
    [JsonPropertyName("temp_unit")] public string TempUnit { get; set; } = "C";

    /// <summary>Boxes to show. Layout: cpu on top; then rows mem|gpu, disk+net|power+sensors, ports|proc. A lone box in a row takes the full width.</summary>
    [JsonPropertyName("boxes")] public List<string> Boxes { get; set; } = new() { "cpu", "mem", "gpu", "disk", "net", "ports", "power", "sensors", "proc" };

    [JsonPropertyName("rounded_corners")] public bool RoundedCorners { get; set; } = true;

    /// <summary>Paint the theme's main_bg. false = keep terminal background.</summary>
    [JsonPropertyName("theme_background")] public bool ThemeBackground { get; set; } = true;

    /// <summary>Process sort: cpu, gpu, mem, pid, name, threads.</summary>
    [JsonPropertyName("proc_sort")] public string ProcSort { get; set; } = "cpu";
    [JsonPropertyName("proc_reversed")] public bool ProcReversed { get; set; } = false;

    /// <summary>Process CPU% relative to one core (htop style, may exceed 100) instead of whole machine.</summary>
    [JsonPropertyName("proc_per_core")] public bool ProcPerCore { get; set; } = false;

    /// <summary>Network adapter name to show. Empty = auto (adapter with default gateway).</summary>
    [JsonPropertyName("net_interface")] public string NetInterface { get; set; } = "";

    /// <summary>Show bits/s instead of bytes/s for network.</summary>
    [JsonPropertyName("net_bits")] public bool NetBits { get; set; } = false;

    /// <summary>Include UDP ports in the ports box (TCP listeners are always shown).</summary>
    [JsonPropertyName("ports_show_udp")] public bool PortsShowUdp { get; set; } = true;

    /// <summary>Ports box sort: "port" (TCP first, then by port number) or "process" (by process name, then port).</summary>
    [JsonPropertyName("ports_sort")] public string PortsSort { get; set; } = "port";

    /// <summary>Drive letters to hide, e.g. ["Z:"].</summary>
    [JsonPropertyName("disk_hide")] public List<string> DiskHide { get; set; } = new();

    /// <summary>Hide fans reporting 0 RPM.</summary>
    [JsonPropertyName("hide_idle_fans")] public bool HideIdleFans { get; set; } = false;

    /// <summary>Relaunch elevated at start (needed for CPU temps, fans, SMART).</summary>
    [JsonPropertyName("require_admin")] public bool RequireAdmin { get; set; } = false;

    /// <summary>Which LibreHardwareMonitor sensor groups to poll.</summary>
    [JsonPropertyName("hardware")] public HardwareConfig Hardware { get; set; } = new();

    [JsonIgnore] public string Path { get; private set; } = "";
    [JsonIgnore] public string? LoadError { get; private set; }

    static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    public static Config Load(string path)
    {
        Config cfg;
        string? err = null;
        if (File.Exists(path))
        {
            try
            {
                cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(path), Opts) ?? new Config();
            }
            catch (Exception ex)
            {
                cfg = new Config();
                err = $"config.json invalid, using defaults: {ex.Message}";
            }
        }
        else
        {
            cfg = new Config();
            try { File.WriteAllText(path, JsonSerializer.Serialize(cfg, Opts)); }
            catch (Exception ex) { err = $"could not write {path}: {ex.Message}"; }
        }
        cfg.Path = path;
        cfg.LoadError = err;
        cfg.Normalize();
        return cfg;
    }

    /// <summary>Persists in-app changes. Never overwrites a config.json that failed to parse.</summary>
    public bool Save()
    {
        if (LoadError != null && File.Exists(Path)) return false;
        try { File.WriteAllText(Path, JsonSerializer.Serialize(this, Opts)); return true; } catch { return false; }
    }

    void Normalize()
    {
        UpdateMs = Math.Clamp(UpdateMs, 250, 10000);
        GraphSymbol = GraphSymbol?.ToLowerInvariant() switch { "block" => "block", "tty" => "tty", _ => "braille" };
        TempUnit = string.Equals(TempUnit, "F", StringComparison.OrdinalIgnoreCase) ? "F" : "C";
        Boxes = (Boxes ?? new()).Select(b => b.Trim().ToLowerInvariant()).ToList();
        ProcSort = ProcSort?.ToLowerInvariant() ?? "cpu";
        PortsSort = string.Equals(PortsSort, "process", StringComparison.OrdinalIgnoreCase) ? "process" : "port";
        DiskHide ??= new();
        Hardware ??= new();
        Theme = string.IsNullOrWhiteSpace(Theme) ? "default" : Theme.Trim();
    }
}
