using System.Text;

namespace WinTop;

public struct Cell : IEquatable<Cell>
{
    public char Ch;
    public Rgb Fg;
    public Rgb? Bg;
    public bool Bold;

    public bool Equals(Cell o) => Ch == o.Ch && Fg == o.Fg && Bg == o.Bg && Bold == o.Bold;
    public override bool Equals(object? obj) => obj is Cell c && Equals(c);
    public override int GetHashCode() => HashCode.Combine(Ch, Fg, Bg, Bold);
}

/// <summary>Off-screen cell buffer rendered to the terminal with 24-bit VT sequences, redrawing only changed rows.</summary>
public sealed class Screen
{
    public int W { get; private set; }
    public int H { get; private set; }
    Cell[] _cells = Array.Empty<Cell>();
    Cell[] _prev = Array.Empty<Cell>();
    bool _full = true;

    public Rgb? DefaultBg;
    public Rgb DefaultFg = new(204, 204, 204);

    public void Resize(int w, int h)
    {
        if (w == W && h == H) return;
        W = Math.Max(1, w);
        H = Math.Max(1, h);
        _cells = new Cell[W * H];
        _prev = new Cell[W * H];
        _full = true;
    }

    public void Invalidate() => _full = true;

    public void Clear()
    {
        var c = new Cell { Ch = ' ', Fg = DefaultFg, Bg = DefaultBg };
        Array.Fill(_cells, c);
    }

    public void Put(int x, int y, char ch, Rgb fg, Rgb? bg = null, bool bold = false)
    {
        if ((uint)x >= (uint)W || (uint)y >= (uint)H) return;
        if (char.IsControl(ch)) ch = ' ';
        ref var c = ref _cells[y * W + x];
        c.Ch = ch;
        c.Fg = fg;
        c.Bg = bg ?? DefaultBg;
        c.Bold = bold;
    }

    /// <summary>Fades everything drawn so far toward a color (keep = share of the original that remains).</summary>
    public void Dim(Rgb toward, double keep)
    {
        for (int i = 0; i < _cells.Length; i++)
        {
            ref var c = ref _cells[i];
            c.Fg = Rgb.Lerp(toward, c.Fg, keep);
            if (c.Bg is Rgb b) c.Bg = Rgb.Lerp(toward, b, keep);
            c.Bold = false;
        }
    }

    public void SetBg(int x, int y, int w, Rgb? bg)
    {
        for (int i = 0; i < w; i++)
            if ((uint)(x + i) < (uint)W && (uint)y < (uint)H) _cells[y * W + x + i].Bg = bg ?? DefaultBg;
    }

    /// <summary>Writes text clipped to maxW columns (or screen edge). Returns x after the text.</summary>
    public int Text(int x, int y, string s, Rgb fg, int maxW = int.MaxValue, bool bold = false, Rgb? bg = null)
    {
        int n = Math.Min(s.Length, maxW);
        for (int i = 0; i < n; i++) Put(x + i, y, s[i], fg, bg, bold);
        return x + Math.Max(0, n);
    }

    public void Fill(int x, int y, int w, int h, char ch, Rgb fg, Rgb? bg = null)
    {
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
                Put(x + i, y + j, ch, fg, bg);
    }

    public string PlainText()
    {
        var sb = new StringBuilder();
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++) sb.Append(_cells[y * W + x].Ch == (char)0 ? ' ' : _cells[y * W + x].Ch);
            sb.Append((char)10);
        }
        return sb.ToString();
    }

    public string Render()
    {
        var sb = new StringBuilder(W * H * 4);
        sb.Append("\x1b[?2026h");
        for (int y = 0; y < H; y++)
        {
            int row = y * W;
            if (!_full && _cells.AsSpan(row, W).SequenceEqual(_prev.AsSpan(row, W))) continue;
            sb.Append("\x1b[").Append(y + 1).Append(";1H");
            Rgb? fg = null;
            Rgb? bg = null;
            bool bgSet = false, bold = false;
            for (int x = 0; x < W; x++)
            {
                var c = _cells[row + x];
                if (!bgSet || c.Bg != bg)
                {
                    if (c.Bg is { } b) sb.Append("\x1b[48;2;").Append(b.R).Append(';').Append(b.G).Append(';').Append(b.B).Append('m');
                    else sb.Append("\x1b[49m");
                    bg = c.Bg;
                    bgSet = true;
                }
                if (fg != c.Fg)
                {
                    sb.Append("\x1b[38;2;").Append(c.Fg.R).Append(';').Append(c.Fg.G).Append(';').Append(c.Fg.B).Append('m');
                    fg = c.Fg;
                }
                if (c.Bold != bold)
                {
                    sb.Append(c.Bold ? "\x1b[1m" : "\x1b[22m");
                    bold = c.Bold;
                }
                sb.Append(c.Ch == '\0' ? ' ' : c.Ch);
            }
            sb.Append("\x1b[0m");
        }
        sb.Append("\x1b[?2026l");
        Array.Copy(_cells, _prev, _cells.Length);
        _full = false;
        return sb.ToString();
    }
}

/// <summary>Rolling history of values.</summary>
public sealed class Ring
{
    readonly double[] _buf;
    int _start, _count;

    public Ring(int capacity = 1024) => _buf = new double[capacity];

    public int Count => _count;

    public void Push(double v)
    {
        if (_count < _buf.Length) _buf[(_start + _count++) % _buf.Length] = v;
        else
        {
            _buf[_start] = v;
            _start = (_start + 1) % _buf.Length;
        }
    }

    /// <summary>i = 0 is the newest sample.</summary>
    public double Back(int i) => i < _count ? _buf[(_start + _count - 1 - i) % _buf.Length] : double.NaN;

    public double MaxLast(int n)
    {
        double m = 0;
        for (int i = 0; i < Math.Min(n, _count); i++) m = Math.Max(m, Back(i));
        return m;
    }
}

/// <summary>Graph and meter primitives in btop style.</summary>
public static class Draw
{
    // Braille dot bits, indexed [column][rowFromBottom]
    static readonly int[,] Dots = { { 0x40, 0x04, 0x02, 0x01 }, { 0x80, 0x20, 0x10, 0x08 } };

    /// <summary>
    /// History graph. Values are 0..100. Newest sample on the right. invert draws top-down (used for upload).
    /// </summary>
    public static void Graph(Screen scr, int x, int y, int w, int h, Ring data, Func<double, double> norm, Gradient grad,
        string symbol, bool invert = false, Rgb? emptyFg = null)
    {
        if (w <= 0 || h <= 0) return;
        int perCell = symbol == "braille" ? 2 : 1;
        int levels = symbol == "braille" ? 4 : 2; // block/tty: half-cell resolution (eighth blocks are missing from many fonts)
        int total = h * levels;

        Span<int> lv = stackalloc int[2];
        for (int cx = 0; cx < w; cx++)
        {
            // values for this column (left, right)

            for (int k = 0; k < perCell; k++)
            {
                int age = (w - 1 - cx) * perCell + (perCell - 1 - k);
                double v = data.Back(age);
                lv[k] = double.IsNaN(v) ? -1 : (int)Math.Round(Math.Clamp(norm(v), 0, 100) / 100.0 * total);
                if (!double.IsNaN(v) && norm(v) > 0.5 && lv[k] == 0) lv[k] = 1; // keep small non-zero values visible
            }
            for (int r = 0; r < h; r++)
            {
                int row = invert ? y + r : y + h - 1 - r; // r counts from the baseline outward
                int baseLvl = r * levels;
                var color = grad.At((r + 0.5) / h * 100);
                char ch;
                if (symbol == "braille")
                {
                    int bits = 0;
                    for (int k = 0; k < 2; k++)
                        for (int d = 0; d < 4; d++)
                            if (lv[k] > baseLvl + d)
                                bits |= Dots[k, invert ? 3 - d : d];
                    ch = bits == 0 ? ' ' : (char)(0x2800 + bits);
                }
                else
                {
                    int fill = Math.Clamp(lv[0] - baseLvl, 0, levels);
                    if (symbol == "block")
                        ch = fill >= 2 ? '█' : fill == 1 ? (invert ? '▀' : '▄') : ' ';
                    else // tty: plain ASCII
                        ch = fill >= 2 ? '#' : fill == 1 ? (invert ? '`' : '.') : ' ';
                }
                if (ch != ' ') scr.Put(x + cx, row, ch, color);
            }
        }
    }

    /// <summary>Horizontal meter of ■ squares, filled part colored along the gradient.</summary>
    public static void Meter(Screen scr, int x, int y, int w, double pct, Gradient grad, Rgb empty, char sym = '■')
    {
        if (w <= 0) return;
        int filled = (int)Math.Round(Math.Clamp(pct, 0, 100) / 100.0 * w);
        if (pct > 0.5 && filled == 0) filled = 1;
        for (int i = 0; i < w; i++)
        {
            if (i < filled) scr.Put(x + i, y, sym, grad.At(w == 1 ? pct : i * 100.0 / (w - 1)));
            else scr.Put(x + i, y, sym, empty);
        }
    }
}
