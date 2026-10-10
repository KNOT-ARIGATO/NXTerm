using System.Text;

namespace NXTerm.Emulation;

[Flags]
public enum CellAttr : ushort
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Italic = 4,
    Underline = 8,
    Blink = 16,
    Reverse = 32,
    Invisible = 64,
    Strike = 128,
    Wide = 256,      // first half of a double-width character
    WideCont = 512,  // second half of a double-width character
}

public struct Cell
{
    public char Ch;
    public string? Comb;  // combining marks (Thai vowels/tone marks) or low surrogate
    public int Fg;        // -1 = default, 0..255 = palette, 0x1000000|rgb = true color
    public int Bg;
    public CellAttr Attr;

    public static Cell Blank(int bg) => new() { Ch = ' ', Fg = -1, Bg = bg };
}

public sealed class TermLine
{
    public Cell[] Cells;
    public bool Wrapped;

    public TermLine(int cols, int bg)
    {
        Cells = new Cell[cols];
        Clear(0, cols, bg);
    }

    public void Clear(int from, int to, int bg)
    {
        to = Math.Min(to, Cells.Length);
        for (int i = Math.Max(0, from); i < to; i++) Cells[i] = Cell.Blank(bg);
    }

    public void Resize(int cols)
    {
        if (cols == Cells.Length) return;
        var n = new Cell[cols];
        Array.Copy(Cells, n, Math.Min(cols, Cells.Length));
        for (int i = Cells.Length; i < cols; i++) n[i] = Cell.Blank(-1);
        Cells = n;
    }

    public string GetText(int from, int to)
    {
        var sb = new StringBuilder();
        to = Math.Min(to, Cells.Length);
        for (int i = Math.Max(0, from); i < to; i++)
        {
            ref var c = ref Cells[i];
            if ((c.Attr & CellAttr.WideCont) != 0) continue;
            sb.Append(c.Ch);
            if (c.Comb != null) sb.Append(c.Comb);
        }
        return sb.ToString();
    }
}

/// <summary>Ring buffer of scrollback lines with O(1) append and drop-oldest.</summary>
public sealed class LineRing
{
    TermLine[] _buf = new TermLine[256];
    int _head, _count;

    public int Count => _count;
    public TermLine this[int i] => _buf[(_head + i) % _buf.Length];

    public void Add(TermLine line)
    {
        if (_count == _buf.Length) Grow();
        _buf[(_head + _count) % _buf.Length] = line;
        _count++;
    }

    public void RemoveFirst()
    {
        if (_count == 0) return;
        _buf[_head] = null!;
        _head = (_head + 1) % _buf.Length;
        _count--;
    }

    public void Clear()
    {
        Array.Clear(_buf);
        _head = _count = 0;
    }

    void Grow()
    {
        var n = new TermLine[_buf.Length * 2];
        for (int i = 0; i < _count; i++) n[i] = this[i];
        _buf = n;
        _head = 0;
    }
}
