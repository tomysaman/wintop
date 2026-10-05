using System.Globalization;
using System.Text.RegularExpressions;

namespace WinTop;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb Lerp(Rgb a, Rgb b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new Rgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    }
}

/// <summary>Three-stop gradient (start/mid/end), 101 precomputed steps like btop.</summary>
public sealed class Gradient
{
    readonly Rgb[] _steps = new Rgb[101];

    public Gradient(Rgb start, Rgb? mid, Rgb end)
    {
        for (int i = 0; i <= 100; i++)
        {
            double t = i / 100.0;
            _steps[i] = mid is { } m
                ? (t < 0.5 ? Rgb.Lerp(start, m, t * 2) : Rgb.Lerp(m, end, (t - 0.5) * 2))
                : Rgb.Lerp(start, end, t);
        }
    }

    public Rgb At(double pct) => _steps[(int)Math.Clamp(Math.Round(pct), 0, 100)];
}

/// <summary>
/// Theme in btop's .theme format: lines of  theme[key]="#rrggbb"  ("#rr" gray, "r g b", or "" for unset).
/// Unknown keys are ignored; keys missing from a theme fall back to related keys, then to the built-in default.
/// </summary>
public sealed class Theme
{
    public string Name { get; private set; } = "default";
    readonly Dictionary<string, Rgb?> _c = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Gradient> _g = new(StringComparer.OrdinalIgnoreCase);

    static readonly Regex Line = new(@"^\s*theme\[\s*([A-Za-z0-9_]+)\s*\]\s*=\s*""?([^""]*)""?\s*$", RegexOptions.Compiled);

    // Fallback chain for keys btop themes don't define.
    static readonly Dictionary<string, string> Fallback = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpu_box"] = "cpu_box",
        ["disk_box"] = "mem_box",
        ["sensors_box"] = "net_box",
        ["power_box"] = "sensors_box",
        ["ports_box"] = "net_box",
        ["gpu_start"] = "cpu_start", ["gpu_mid"] = "cpu_mid", ["gpu_end"] = "cpu_end",
        ["fan_start"] = "temp_start", ["fan_mid"] = "temp_mid", ["fan_end"] = "temp_end",
        ["disk_start"] = "used_start", ["disk_mid"] = "used_mid", ["disk_end"] = "used_end",
        ["swap_start"] = "available_start", ["swap_mid"] = "available_mid", ["swap_end"] = "available_end",
        ["signal_start"] = "free_start", ["signal_mid"] = "free_mid", ["signal_end"] = "free_end",
        ["box_title"] = "title",
    };

    public static Theme Parse(string name, string text, Theme? fallback)
    {
        var t = new Theme { Name = name };
        if (fallback != null)
            foreach (var kv in fallback._c) t._c[kv.Key] = kv.Value;
        var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var m = Line.Match(line);
            if (!m.Success) continue;
            t._c[m.Groups[1].Value] = ParseColor(m.Groups[2].Value.Trim());
            own.Add(m.Groups[1].Value);
        }
        // A btop theme that sets cpu_box but not gpu_box should get its own cpu_box color, not the default gpu_box.
        foreach (var (key, fb) in Fallback)
            if (!own.Contains(key) && own.Contains(fb) && t._c.TryGetValue(fb, out var v)) t._c[key] = v;
        t.BuildGradients();
        return t;
    }

    static Rgb? ParseColor(string v)
    {
        if (v.Length == 0) return null;
        if (v.StartsWith('#'))
        {
            var h = v[1..];
            if (h.Length == 2 && byte.TryParse(h, NumberStyles.HexNumber, null, out var gray)) return new Rgb(gray, gray, gray);
            if (h.Length == 6 && int.TryParse(h, NumberStyles.HexNumber, null, out var rgb))
                return new Rgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            if (h.Length == 3 && int.TryParse(h, NumberStyles.HexNumber, null, out var s))
                return new Rgb((byte)(((s >> 8) & 0xF) * 17), (byte)(((s >> 4) & 0xF) * 17), (byte)((s & 0xF) * 17));
            return null;
        }
        var parts = v.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 && parts.All(p => byte.TryParse(p, out _)))
            return new Rgb(byte.Parse(parts[0]), byte.Parse(parts[1]), byte.Parse(parts[2]));
        return null;
    }

    void BuildGradients()
    {
        foreach (var g in new[] { "temp", "cpu", "free", "cached", "available", "used", "download", "upload", "process",
                     "gpu", "fan", "disk", "swap", "signal" })
        {
            var s = Get(g + "_start");
            var e = Get(g + "_end");
            var m = Get(g + "_mid");
            if (s == null && e == null) continue;
            _g[g] = new Gradient(s ?? e!.Value, m, e ?? s!.Value);
        }
    }

    Rgb? Get(string key)
    {
        for (int depth = 0; depth < 4 && key != null; depth++)
        {
            if (_c.TryGetValue(key, out var v) && v != null) return v;
            if (!Fallback.TryGetValue(key, out key!)) break;
        }
        return null;
    }

    /// <summary>Color or null when the theme leaves it unset (e.g. transparent main_bg).</summary>
    public Rgb? Opt(string key) => Get(key);

    public Rgb this[string key] => Get(key) ?? (key.EndsWith("bg") ? new Rgb(0, 0, 0) : new Rgb(204, 204, 204));

    public Gradient Grad(string name) => _g.TryGetValue(name, out var g) ? g : _g["cpu"];
}

public static class ThemeStore
{
    public const string Ext = ".theme";

    /// <summary>Writes the bundled themes into theme\ if they are missing.</summary>
    public static void EnsureDefaults(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var asm = typeof(ThemeStore).Assembly;
            foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith("theme.")))
            {
                var file = Path.Combine(dir, res["theme.".Length..].ToLowerInvariant());
                if (File.Exists(file)) continue;
                using var s = asm.GetManifestResourceStream(res)!;
                using var f = File.Create(file);
                s.CopyTo(f);
            }
        }
        catch { }
    }

    public static string BuiltinDefault()
    {
        var asm = typeof(ThemeStore).Assembly;
        var res = asm.GetManifestResourceNames().First(n => string.Equals(n, "theme.default.theme", StringComparison.OrdinalIgnoreCase));
        using var s = asm.GetManifestResourceStream(res)!;
        return new StreamReader(s).ReadToEnd();
    }

    /// <summary>Themes are theme\name.theme files, or theme\name\ folders containing a .theme file.</summary>
    public static List<string> List(string dir)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*" + Ext)) names.Add(Path.GetFileNameWithoutExtension(f));
            foreach (var d in Directory.EnumerateDirectories(dir))
                if (Directory.EnumerateFiles(d, "*" + Ext).Any()) names.Add(Path.GetFileName(d));
        }
        catch { }
        return names.ToList();
    }

    public static Theme Load(string dir, string name, out string? error)
    {
        error = null;
        var builtin = Theme.Parse("builtin", BuiltinDefault(), null);
        string? path = null;
        var file = Path.Combine(dir, name + Ext);
        var folder = Path.Combine(dir, name);
        if (File.Exists(file)) path = file;
        else if (Directory.Exists(folder))
        {
            var preferred = Path.Combine(folder, "theme" + Ext);
            path = File.Exists(preferred) ? preferred : Directory.EnumerateFiles(folder, "*" + Ext).FirstOrDefault();
        }
        if (path == null)
        {
            error = $"theme '{name}' not found in {dir}";
            return builtin;
        }
        try { return Theme.Parse(name, File.ReadAllText(path), builtin); }
        catch (Exception ex)
        {
            error = $"theme '{name}': {ex.Message}";
            return builtin;
        }
    }
}
