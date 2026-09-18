using System.Text.RegularExpressions;

namespace TexBowser.Render;

/// <summary>
/// Pure mouse hit-testing: rendered pages embed followable "[n]" markers
/// (page links, nav strips, history entries). Given a document line and a
/// character column, returns the 1-based link index under the cursor.
/// </summary>
public static class LinkHitTest
{
    private static readonly Regex Marker = new(@"\[(\d+)\]", RegexOptions.Compiled);

    public static int? FindLinkAt(string? line, int col) =>
        FindLinkSpan(line, col)?.Index;

    /// <summary>Index plus exact character span of the marker under col.</summary>
    public static (int Index, int Start, int Length)? FindLinkSpan(string? line, int col)
    {
        if (string.IsNullOrEmpty(line) || col < 0) return null;
        foreach (Match m in Marker.Matches(line))
        {
            if (col >= m.Index && col < m.Index + m.Length
                && int.TryParse(m.Groups[1].Value, out int n) && n >= 1)
                return (n, m.Index, m.Length);
        }
        return null;
    }

    /// <summary>All "[n]" markers in a line, for overlaying clickable buttons. Generic.</summary>
    public static List<(int Index, int Start, int Length)> FindAllSpans(string? line)
    {
        var out_ = new List<(int, int, int)>();
        if (string.IsNullOrEmpty(line)) return out_;
        foreach (Match m in Marker.Matches(line))
        {
            if (int.TryParse(m.Groups[1].Value, out int n) && n >= 1)
                out_.Add((n, m.Index, m.Length));
        }
        return out_;
    }
}
