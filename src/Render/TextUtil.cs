using System.Text;

namespace TexBowser.Render;

/// <summary>
/// Text-grid helpers: word wrap, rules, side-by-side column grids and bordered tables.
/// Everything here works in character cells so column widths stay pixel-exact.
/// </summary>
public static class TextUtil
{
    public const string ColSep = " │ ";

    /// <summary>
    /// Word-wrap preserving explicit newlines; over-long words are hard-broken.
    /// Span-based: no Split arrays, no Trim copies, no word substrings. Each
    /// output line is a single allocation; the StringBuilder buffer is tiny
    /// (≤ width) and reused. No stackalloc (safe for big text).
    /// </summary>
    public static List<string> Wrap(string text, int width)
    {
        if (width < 4) width = 4;
        var result = new List<string>();
        string srcStr = text ?? "";
        ReadOnlySpan<char> src = srcStr.AsSpan();
        var sb = new StringBuilder(width + 16);
        int pos = 0;
        while (true)
        {
            int rel = src.Slice(pos).IndexOf('\n');
            int end = rel < 0 ? src.Length : pos + rel;
            // Trim the raw range in place (no Trim() copy).
            int s = pos, e = end;
            while (s < e && char.IsWhiteSpace(src[s])) s++;
            while (e > s && char.IsWhiteSpace(src[e - 1])) e--;
            if (s >= e)
            {
                result.Add("");
            }
            else
            {
                sb.Clear();
                int i = s;
                while (i < e)
                {
                    // Split on ' ' only (matches Split(' ', RemoveEmptyEntries)).
                    while (i < e && src[i] == ' ') i++;
                    if (i >= e) break;
                    int j = i;
                    while (j < e && src[j] != ' ') j++;
                    int wStart = i, wLen = j - i;
                    while (wLen > width)
                    {
                        if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
                        int cs = wStart;
                        result.Add(string.Create(width, (srcStr, cs), static (span, st) =>
                            st.srcStr.AsSpan(st.cs, span.Length).CopyTo(span)));
                        wStart += width;
                        wLen -= width;
                    }
                    if (sb.Length == 0) sb.Append(srcStr, wStart, wLen);
                    else if (sb.Length + 1 + wLen <= width) sb.Append(' ').Append(srcStr, wStart, wLen);
                    else { result.Add(sb.ToString()); sb.Clear(); sb.Append(srcStr, wStart, wLen); }
                    i = j;
                }
                if (sb.Length > 0) result.Add(sb.ToString());
            }
            if (rel < 0) break;
            pos = end + 1;
        }
        return result;
    }

    public static List<string> WrapAll(IEnumerable<string> paragraphs, int width)
    {
        var result = new List<string>();
        bool first = true;
        foreach (var p in paragraphs)
        {
            if (!first) result.Add("");
            first = false;
            result.AddRange(Wrap(p, width));
        }
        return result;
    }

    /// <summary>Fit exactly to width: pad or truncate. Single alloc via string.Create.</summary>
    public static string Fit(string s, int width) => TextConcat.Fit(s, width);

    public static string Rule(int width, string title = "") => TextConcat.Rule(width, title);

    /// <summary>Split a total into integer widths proportional to fracs (largest remainder).</summary>
    public static int[] PixelWidths(double[] fracs, int total)
    {
        int n = fracs.Length;
        var widths = new int[n];
        var rem = new double[n];
        double sum = fracs.Sum();
        if (sum <= 0) sum = n;
        int used = 0;
        for (int i = 0; i < n; i++)
        {
            double exact = fracs[i] / sum * total;
            widths[i] = (int)Math.Floor(exact);
            rem[i] = exact - widths[i];
            used += widths[i];
        }
        int left = total - used;
        foreach (var i in Enumerable.Range(0, n).OrderByDescending(i => rem[i]))
        {
            if (left <= 0) break;
            widths[i]++;
            left--;
        }
        return widths;
    }

    /// <summary>Join pre-wrapped columns side by side with a │ separator.</summary>
    public static List<string> RenderColumnGrid(
        IReadOnlyList<IReadOnlyList<string>> columns,
        IReadOnlyList<int> widths,
        string sep = ColSep)
    {
        int height = columns.Count == 0 ? 0 : columns.Max(c => c.Count);
        var lines = new List<string>(height);
        // Reusable cell buffer: no per-cell Fit strings, no StringBuilder,
        // no TrimEnd second allocation. Each line is one string.Create.
        var row = new List<string>(columns.Count);
        for (int r = 0; r < height; r++)
        {
            row.Clear();
            for (int c = 0; c < columns.Count; c++)
                row.Add(r < columns[c].Count ? columns[c][r] : "");
            lines.Add(TextConcat.ColumnLine(row, widths, sep));
        }
        return lines;
    }

    /// <summary>Greedy horizontal nav strip: "a │ b │ c" flowing onto multiple lines.</summary>
    public static List<string> WrapStrip(IReadOnlyList<string> items, int width, string sep = " │ ")
    {
        var lines = new List<string>();
        var sb = new StringBuilder();
        foreach (var item in items)
        {
            var it = item.Trim();
            if (it.Length == 0) continue;
            if (sb.Length == 0) sb.Append(it);
            else if (sb.Length + sep.Length + it.Length <= width) sb.Append(sep).Append(it);
            else
            {
                lines.Add(sb.ToString());
                sb.Clear();
                // A single over-long item gets wrapped on its own.
                if (it.Length > width) lines.AddRange(Wrap(it, width));
                else sb.Append(it);
            }
        }
        if (sb.Length > 0) lines.Add(sb.ToString());
        return lines;
    }

    /// <summary>Bordered data table that shrinks/wraps to fit maxWidth.</summary>
    public static List<string> RenderDataTable(
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string>> rows,
        int maxWidth)
    {
        int cols = Math.Max(headers.Count, rows.Count == 0 ? 0 : rows.Max(r => r.Count));
        if (cols == 0) return new List<string>();
        var norm = rows.Select(r =>
        {
            var list = r.Select(c => OneLine(c)).ToList();
            while (list.Count < cols) list.Add("");
            return (IReadOnlyList<string>)list;
        }).ToList();
        var head = headers.Select(OneLine).ToList();
        while (head.Count < cols) head.Add("");

        var nat = new int[cols];
        for (int c = 0; c < cols; c++)
        {
            int m = head[c].Length;
            foreach (var r in norm) m = Math.Max(m, r[c].Length);
            nat[c] = Math.Clamp(m, 1, 48);
        }
        int totalNat = nat.Sum() + 3 * cols + 1;
        int[] widths;
        if (totalNat <= maxWidth)
        {
            widths = nat;
        }
        else
        {
            int avail = maxWidth - 3 * cols - 1;
            int minNeed = 4 * cols;
            if (avail < minNeed) avail = minNeed; // tiny viewport: allow slight overflow
            double sumNat = nat.Sum();
            widths = new int[cols];
            int used = 0;
            for (int c = 0; c < cols; c++)
            {
                widths[c] = Math.Max(4, (int)Math.Floor(nat[c] / sumNat * avail));
                used += widths[c];
            }
            int diff = avail - used;
            var order = diff >= 0
                ? Enumerable.Range(0, cols).OrderByDescending(c => nat[c]).ToList()
                : Enumerable.Range(0, cols).OrderBy(c => widths[c]).ToList();
            int i = 0;
            while (diff != 0 && order.Count > 0)
            {
                int c = order[i % order.Count];
                if (diff > 0) { widths[c]++; diff--; }
                else if (widths[c] > 4) { widths[c]--; diff++; }
                i++;
                if (i > 10000) break;
            }
        }

        string Border(string l, string m, string r) =>
            TextConcat.Border(l, m, r, widths);

        var lines = new List<string> { Border("┌", "┬", "┐") };
        var headWrapped = head.Select((h, c) => Wrap(h, widths[c])).ToList();
        lines.AddRange(RowLines(headWrapped, widths));
        lines.Add(Border("├", "┼", "┤"));
        foreach (var row in norm)
        {
            var wrapped = row.Select((cell, c) => Wrap(cell, widths[c])).ToList();
            lines.AddRange(RowLines(wrapped, widths));
        }
        lines.Add(Border("└", "┴", "┘"));
        return lines;
    }

    private static List<string> RowLines(List<List<string>> wrappedCells, int[] widths)
    {
        int height = wrappedCells.Max(c => c.Count);
        var lines = new List<string>();
        var row = new List<string>(wrappedCells.Count);
        for (int r = 0; r < height; r++)
        {
            row.Clear();
            for (int c = 0; c < wrappedCells.Count; c++)
                row.Add(r < wrappedCells[c].Count ? wrappedCells[c][r] : "");
            lines.Add(TextConcat.TableRowLine(row, widths));
        }
        return lines;
    }

    private static string OneLine(string? s) => TextConcat.OneLine(s);
}
