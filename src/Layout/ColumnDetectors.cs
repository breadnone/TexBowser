using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace TexBowser.Layout;

/// <summary>
/// Column geometry, detected in priority order (strongest signal first):
/// 1. grid-template  — CSS grid tracks incl. repeat()/minmax()
/// 2. flex           — flex-grow/basis/widths on a display:flex row
/// 3. framework      — parent tracks (Tailwind grid-cols-N, Bulma columns, Foundation grid-x)
/// 4. spans          — child size classes (Bootstrap col-*-N, Tailwind w-1/3,
///                     Bulma is-*, Foundation cell, 960gs grid_N)
/// 5. width% / width px — effective widths (inline style or &lt;style&gt; rules)
/// 6. float          — floated siblings (classic float layouts)
/// 7. cards          — identical repeated siblings (card grids)
///
/// Fractions are RAW (pre-normalization) so the segmenter can tell a wrapped
/// 4×w-1/2 (two visual rows) from a true 4-column row. RowSize = cells per
/// visual row when the detector knows the track count; Spans enables
/// Bootstrap-style greedy row wrapping.
/// </summary>
public sealed record ColDetect(double[] Fracs, int[]? Spans, string Source, int? RowSize);

public static class ColumnDetectors
{
    public static ColDetect? Detect(HtmlNode container, StyleLookup styles)
    {
        var kids = container.ChildNodes
            .Where(n => n.NodeType == HtmlNodeType.Element && !Sanitizer.IsHidden(n, styles)).ToList();
        if (kids.Count < 2 || kids.Count > 12) return null;
        // Section headers (e.g. <span>Popular</span> + <ul> links, or <h2> + content)
        // are stacked, never side-by-side columns, even when CSS says flex/grid.
        if (IsSectionHeader(kids)) return null;
        return GridTemplate(container, kids, styles)
            ?? Flex(container, kids, styles)
            ?? FrameworkParent(container, kids)
            ?? FrameworkChildren(kids)
            ?? WidthPct(kids, styles)
            ?? WidthPx(kids, styles)
            ?? FloatRow(kids, styles)
            ?? CardRow(kids);
    }

    /// <summary>
    /// Header-plus-content pattern: one short header-like child (span/h/label or
    /// trending label) beside much longer content (typically a list). Such
    /// containers (e.g. pcgamer trending bar: Popular + 6 links) must stack
    /// the header above the list, not split into columns.
    /// </summary>
    private static bool IsSectionHeader(List<HtmlNode> kids)
    {
        foreach (var k in kids)
        {
            string tag = k.Name.ToLowerInvariant();
            bool headerTag = tag is "h1" or "h2" or "h3" or "h4" or "h5" or "h6"
                or "header" or "span" or "label";
            if (!headerTag) continue;
            int len = BlockTextLength(k);
            if (len == 0 || len > 60) continue;
            // A list sibling is the classic section pattern (label + <ul>).
            bool hasListSibling = kids.Any(x => !ReferenceEquals(x, k) &&
                (x.Name.Equals("ul", StringComparison.OrdinalIgnoreCase)
                 || x.Name.Equals("ol", StringComparison.OrdinalIgnoreCase)));
            if (hasListSibling) return true;
            int rest = 0;
            foreach (var x in kids)
                if (!ReferenceEquals(x, k)) rest += BlockTextLength(x);
            if (rest > Math.Max(100, len * 3)) return true;
        }
        return false;
    }

    // ---------------- 1. CSS grid tracks ----------------

    private static ColDetect? GridTemplate(HtmlNode container, List<HtmlNode> kids, StyleLookup styles)
    {
        string disp = DisplayOf(container, styles);
        if (!disp.Contains("grid")) return null;
        string? tpl = StyleLookup.Get(styles.Effective(container), "grid-template-columns");
        if (string.IsNullOrWhiteSpace(tpl)) return null;
        var weights = ParseTracks(tpl);
        if (weights.Count < 2 || weights.Count > 12) return null;
        double sum = weights.Sum();
        if (sum <= 0) return null;
        var fracs = weights.Select(w => w / sum).ToArray();
        int? rowSize = kids.Count > weights.Count ? weights.Count : null;
        return new ColDetect(fracs, null, "grid", rowSize);
    }

    private static List<double> ParseTracks(string tpl)
    {
        tpl = ExpandRepeats(tpl);
        var weights = new List<double>();
        foreach (var track in SplitTopLevel(tpl))
        {
            string s = track.Trim().ToLowerInvariant();
            if (s.Length == 0) continue;
            var mm = Regex.Match(s, @"minmax\([^,]+,\s*([^)]+)\)");
            if (mm.Success) s = mm.Groups[1].Value.Trim();
            var fr = Regex.Match(s, @"^([\d.]+)\s*fr$");
            if (fr.Success && double.TryParse(fr.Groups[1].Value, out double f) && f > 0)
            {
                weights.Add(f);
                continue;
            }
            var pc = Regex.Match(s, @"^([\d.]+)\s*%$");
            if (pc.Success && double.TryParse(pc.Groups[1].Value, out double p) && p > 0)
            {
                weights.Add(p / 100);
                continue;
            }
            var px = Regex.Match(s, @"^([\d.]+)\s*px$");
            if (px.Success && double.TryParse(px.Groups[1].Value, out double x) && x > 0)
            {
                weights.Add(x / 800); // fixed px vs unknown container: assume ~800px canvas
                continue;
            }
            weights.Add(1); // auto, min/max-content, fit-content, ...
        }
        return weights;
    }

    /// <summary>Expand repeat(N, X) — paren-aware so minmax()/fit-content() survive.</summary>
    private static string ExpandRepeats(string tpl)
    {
        while (true)
        {
            var m = Regex.Match(tpl, @"repeat\(\s*(\d+)\s*,", RegexOptions.IgnoreCase);
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out int n)) return tpl;
            int paren = m.Index + "repeat".Length; // index of '('
            int depth = 0, end = -1;
            for (int i = paren; i < tpl.Length; i++)
            {
                if (tpl[i] == '(') depth++;
                else if (tpl[i] == ')') { depth--; if (depth == 0) { end = i; break; } }
            }
            if (end < 0) return tpl;
            string inner = tpl[(m.Index + m.Value.Length)..end].Trim();
            n = Math.Clamp(n, 1, 12);
            tpl = tpl[..m.Index] + string.Join(" ", Enumerable.Repeat(inner, n)) + tpl[(end + 1)..];
        }
    }

    private static List<string> SplitTopLevel(string s)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        int depth = 0;
        foreach (char c in s)
        {
            if (c == '(') depth++;
            if (c == ')') depth = Math.Max(0, depth - 1);
            if ((c == ' ' || c == '\t') && depth == 0)
            {
                if (sb.Length > 0) { parts.Add(sb.ToString()); sb.Clear(); }
            }
            else sb.Append(c);
        }
        if (sb.Length > 0) parts.Add(sb.ToString());
        return parts;
    }

    // ---------------- 2. flexbox ----------------

    private static ColDetect? Flex(HtmlNode container, List<HtmlNode> kids, StyleLookup styles)
    {
        string disp = DisplayOf(container, styles);
        if (!disp.Contains("flex")) return null;
        if (FlexDirOf(container, styles).Contains("column")) return null; // vertical stack

        var grows = new List<double>();
        var fixed_ = new List<double?>();
        foreach (var k in kids)
        {
            var e = styles.Effective(k);
            grows.Add(FlexGrow(k, e));
            fixed_.Add(FixedFrac(k, e));
        }

        bool anyGrow = grows.Any(g => g > 0);
        bool anyFixed = fixed_.Any(f => f != null);
        if (!anyGrow && !anyFixed)
        {
            if (kids.Count > 6) return null; // long un-sized flex runs (menus) stay stacked
            // Un-sized flex with short items (link strips, trending bars, menus)
            // stacks instead of becoming cramped equal columns.
            if (kids.Any(k => BlockTextLength(k) < 20)) return null;
            return new ColDetect(Equal(kids.Count), null, "flex", null);
        }
        if (anyGrow && !anyFixed)
            return new ColDetect(grows.ToArray(), null, "flex", null);
        if (anyFixed && fixed_.Count(f => f != null) >= 2)
        {
            // Fixed parts keep their share; flexible parts split the remainder.
            double known = fixed_.Where(f => f != null).Sum(f => f!.Value);
            int flexN = grows.Count(g => g > 0);
            var fracs = new double[kids.Count];
            for (int i = 0; i < kids.Count; i++)
                fracs[i] = fixed_[i] ?? (flexN > 0 ? Math.Max(0, 1 - known) / flexN : 0);
            if (fracs.All(f => f <= 0)) return null;
            return new ColDetect(fracs, null, "flex", null);
        }
        return null;
    }

    private static double FlexGrow(HtmlNode k, Dictionary<string, string> e)
    {
        foreach (var c in StyleLookup.ClassesOf(k))
        {
            string lc = c.ToLowerInvariant();
            if (lc is "flex-1" or "flex-auto" or "flex-grow") return 1;
            if (lc is "flex-initial" or "flex-none" or "flex-grow-0") return 0;
        }
        string? flex = StyleLookup.Get(e, "flex");
        if (flex != null)
        {
            if (flex == "none") return 0;
            if (flex.StartsWith("auto")) return 1;
            var m = Regex.Match(flex, @"^([\d.]+)");
            if (m.Success && double.TryParse(m.Groups[1].Value, out double v)) return v;
        }
        string? g = StyleLookup.Get(e, "flex-grow");
        if (g != null && double.TryParse(g, out double gv)) return gv;
        return 0;
    }

    private static double? FixedFrac(HtmlNode k, Dictionary<string, string> e)
    {
        string? w = StyleLookup.Get(e, "width");
        if (w != null)
        {
            var m = Regex.Match(w, @"^([\d.]+)\s*%$");
            if (m.Success && double.TryParse(m.Groups[1].Value, out double p) && p >= 5 && p <= 95)
                return p / 100;
        }
        string? b = StyleLookup.Get(e, "flex-basis");
        if (b != null)
        {
            var m = Regex.Match(b, @"^([\d.]+)\s*%$");
            if (m.Success && double.TryParse(m.Groups[1].Value, out double p) && p >= 5 && p <= 95)
                return p / 100;
        }
        return TailwindFrac(k);
    }

    // ---------------- 3. framework parent tracks ----------------

    private static ColDetect? FrameworkParent(HtmlNode container, List<HtmlNode> kids)
    {
        var cls = StyleLookup.ClassesOf(container).Select(c => c.ToLowerInvariant()).ToList();
        int? tracks = GridColsCount(cls);
        bool hasColSpan = kids.Any(k => StyleLookup.ClassesOf(k)
            .Any(c => Regex.IsMatch(c, @"(^|:)col-span-\d+$", RegexOptions.IgnoreCase)));
        if (tracks is { } n && n >= 2 && n <= 12 && !hasColSpan)
        {
            int? rowSize = kids.Count > n ? n : null;
            return new ColDetect(Equal(n), null, "tailwind", rowSize);
        }
        bool bulma = cls.Contains("columns");
        bool foundation = cls.Contains("grid-x");
        if ((bulma || foundation) && !kids.Any(HasSizeClass))
            return new ColDetect(Equal(kids.Count), null, bulma ? "bulma" : "foundation", null);
        return null;
    }

    private static readonly string[] BpPriority = { "2xl", "xl", "lg", "md", "sm", "" };

    private static int? GridColsCount(List<string> containerClasses)
    {
        foreach (var bp in BpPriority)
        {
            foreach (var c in containerClasses)
            {
                string rest = bp.Length == 0 ? c : (c.StartsWith(bp + ":") ? c[(bp.Length + 1)..] : "");
                if (rest.Length == 0) continue;
                var m = Regex.Match(rest, @"^grid-cols-(\d+)$");
                if (m.Success && int.TryParse(m.Groups[1].Value, out int n)) return n;
            }
        }
        return null;
    }

    private static bool HasSizeClass(HtmlNode k)
    {
        foreach (var c in StyleLookup.ClassesOf(k))
        {
            string lc = c.ToLowerInvariant();
            if (Regex.IsMatch(lc, @"^col-(?:(?:xs|sm|md|lg|xl|xxl)-)?\d{1,2}$")) return true;
            if (Regex.IsMatch(lc, @"^(?:[a-z0-9]+:)?(w|basis)-(\d+\/\d+|full)$")) return true;
            if (Regex.IsMatch(lc, @"^(?:small|medium|large)-\d{1,2}$")) return true;
            if (Regex.IsMatch(lc, @"^grid_\d{1,2}$")) return true;
            if (BulmaSize(lc) != null) return true;
        }
        return false;
    }

    // ---------------- 4. framework child sizes ----------------

    private sealed record SizeInfo(double Frac, int? Span12, string System);

    private static ColDetect? FrameworkChildren(List<HtmlNode> kids)
    {
        var infos = kids.Select(SizeInfoOf).ToList();
        if (infos.Any(i => i == null)) return null;
        var list = infos.Select(i => i!).ToList();
        if (list.Any(i => i.Frac <= 0 || i.Frac > 1)) return null;

        string system = list.Select(i => i.System).Distinct().Count() == 1
            ? list[0].System : "mixed";

        if (list.All(i => i.Span12 != null))
        {
            var spans = list.Select(i => i.Span12!.Value).ToArray();
            double sum = spans.Sum();
            if (sum <= 0 || sum > 48) return null;
            return new ColDetect(spans.Select(s => s / 12.0).ToArray(), spans, system, null);
        }
        return new ColDetect(list.Select(i => i.Frac).ToArray(), null, system, null);
    }

    private static SizeInfo? SizeInfoOf(HtmlNode k)
    {
        var classes = StyleLookup.ClassesOf(k).Select(c => c.ToLowerInvariant()).ToList();

        int? bs = BootstrapSpan(classes);
        if (bs != null) return new SizeInfo(bs.Value / 12.0, bs, "bootstrap");

        int? fs = FoundationSpan(classes);
        if (fs != null) return new SizeInfo(fs.Value / 12.0, fs, "foundation");

        foreach (var c in classes)
        {
            var m = Regex.Match(c, @"^grid_(\d{1,2})$"); // 960gs
            if (m.Success && int.TryParse(m.Groups[1].Value, out int g) && g >= 1 && g <= 12)
                return new SizeInfo(g / 12.0, g, "960gs");
        }

        foreach (var c in classes)
        {
            var b = BulmaSize(c);
            if (b != null) return b;
        }

        double? tw = TailwindFrac(k);
        if (tw != null) return new SizeInfo(tw.Value, null, "tailwind");

        return null;
    }

    private static int? BootstrapSpan(List<string> classes)
    {
        // Desktop-first breakpoint preference.
        foreach (var bp in new[] { "xl", "lg", "md", "sm", "xs", "" })
        {
            foreach (var c in classes)
            {
                var m = Regex.Match(c, @"^col-(?:(xs|sm|md|lg|xl|xxl)-)?(\d{1,2})$");
                if (!m.Success) continue;
                string found = m.Groups[1].Success ? m.Groups[1].Value : "";
                if (found == bp && int.TryParse(m.Groups[2].Value, out int s) && s >= 1 && s <= 12)
                    return s;
            }
        }
        return null;
    }

    private static int? FoundationSpan(List<string> classes)
    {
        foreach (var bp in new[] { "large", "medium", "small" })
        {
            foreach (var c in classes)
            {
                var m = Regex.Match(c, @"^(small|medium|large)-(\d{1,2})$");
                if (!m.Success || m.Groups[1].Value != bp) continue;
                if (int.TryParse(m.Groups[2].Value, out int s) && s >= 1 && s <= 12) return s;
            }
        }
        return null;
    }

    private static SizeInfo? BulmaSize(string c)
    {
        double? f = c switch
        {
            "is-full" => 1.0,
            "is-three-quarters" => 0.75,
            "is-two-thirds" => 2.0 / 3,
            "is-half" => 0.5,
            "is-one-third" => 1.0 / 3,
            "is-one-quarter" => 0.25,
            "is-one-fifth" => 0.2,
            "is-two-fifths" => 0.4,
            "is-three-fifths" => 0.6,
            "is-four-fifths" => 0.8,
            _ => null,
        };
        if (f != null) return new SizeInfo(f.Value, null, "bulma");
        var m = Regex.Match(c, @"^is-(\d{1,2})$");
        if (m.Success && int.TryParse(m.Groups[1].Value, out int n) && n >= 1 && n <= 12)
            return new SizeInfo(n / 12.0, n, "bulma");
        return null;
    }

    /// <summary>Tailwind w-1/3 / basis-1/2 (desktop-first prefix preference).</summary>
    public static double? TailwindFrac(HtmlNode k)
    {
        var classes = StyleLookup.ClassesOf(k).Select(c => c.ToLowerInvariant()).ToList();
        foreach (var bp in BpPriority)
        {
            foreach (var c in classes)
            {
                string rest = bp.Length == 0 ? c : (c.StartsWith(bp + ":") ? c[(bp.Length + 1)..] : "");
                if (rest.Length == 0) continue;
                var m = Regex.Match(rest, @"^(w|basis)-(.+)$");
                if (!m.Success) continue;
                string v = m.Groups[2].Value;
                if (v == "full") return 1.0;
                var f = Regex.Match(v, @"^(\d+)\/(\d+)$");
                if (f.Success && double.TryParse(f.Groups[1].Value, out double a)
                    && double.TryParse(f.Groups[2].Value, out double b) && b > 0)
                {
                    double frac = a / b;
                    if (frac > 0 && frac <= 1) return frac;
                }
            }
        }
        return null;
    }

    // ---------------- 5/6. widths & floats (effective styles) ----------------

    private static ColDetect? WidthPct(List<HtmlNode> kids, StyleLookup styles)
    {
        var vals = new List<double>();
        foreach (var k in kids)
        {
            var w = StyleLookup.Get(styles.Effective(k), "width");
            var m = w != null ? Regex.Match(w, @"^([\d.]+)\s*%$") : Match.Empty;
            if (!m.Success || !double.TryParse(m.Groups[1].Value, out double p) || p < 5 || p > 100)
                return null;
            vals.Add(p);
        }
        if (vals.Sum() < 40) return null;
        double sum = vals.Sum();
        return new ColDetect(vals.Select(v => v / sum).ToArray(), null, "width%", null);
    }

    private static ColDetect? WidthPx(List<HtmlNode> kids, StyleLookup styles)
    {
        var vals = new List<double>();
        foreach (var k in kids)
        {
            var w = StyleLookup.Get(styles.Effective(k), "width");
            var m = w != null ? Regex.Match(w, @"^([\d.]+)\s*px$") : Match.Empty;
            if (!m.Success || !double.TryParse(m.Groups[1].Value, out double p) || p < 20) return null;
            vals.Add(p);
        }
        double sum = vals.Sum();
        return new ColDetect(vals.Select(v => v / sum).ToArray(), null, "widthpx", null);
    }

    private static ColDetect? FloatRow(List<HtmlNode> kids, StyleLookup styles)
    {
        if (kids.Count > 4) return null; // bigger floated runs are usually menus
        int floated = kids.Count(k =>
            StyleLookup.Get(styles.Effective(k), "float") is "left" or "right"
            || FloatClass(k) != null);
        if (floated < 2) return null;
        var fracs = new List<double>();
        foreach (var k in kids)
        {
            double? fix = FixedFrac(k, styles.Effective(k));
            fracs.Add(fix ?? -1);
        }
        double[] f = fracs.Select(v => v < 0 ? 0 : v).ToArray();
        if (f.All(v => v == 0)) f = Equal(kids.Count);
        else if (f.Any(v => v == 0))
        {
            double known = f.Sum();
            int rest = f.Count(v => v == 0);
            for (int i = 0; i < f.Length; i++)
                if (f[i] == 0) f[i] = Math.Max(0, 1 - known) / rest;
        }
        double sum = f.Sum();
        if (sum <= 0) return null;
        return new ColDetect(f.Select(v => v / sum).ToArray(), null, "float", null);
    }

    // ---------------- display / direction inference (incl. utilities) ----------------

    /// <summary>display from effective style, else utility classes (flex/grid).</summary>
    private static string DisplayOf(HtmlNode el, StyleLookup styles)
    {
        string? d = StyleLookup.Get(styles.Effective(el), "display");
        if (!string.IsNullOrEmpty(d)) return d;
        foreach (var c in StyleLookup.ClassesOf(el))
        {
            string s = StripBp(c.ToLowerInvariant());
            if (s is "flex" or "inline-flex") return "flex";
            if (s is "grid" or "inline-grid") return "grid";
        }
        return "";
    }

    /// <summary>flex direction, desktop-first across responsive prefixes.</summary>
    private static string FlexDirOf(HtmlNode el, StyleLookup styles)
    {
        string? d = StyleLookup.Get(styles.Effective(el), "flex-direction");
        if (!string.IsNullOrEmpty(d)) return d;
        foreach (var bp in BpPriority)
        {
            foreach (var c in StyleLookup.ClassesOf(el))
            {
                string lc = c.ToLowerInvariant();
                string rest = bp.Length == 0
                    ? (lc.Contains(':') ? "" : lc)
                    : (lc.StartsWith(bp + ":") ? lc[(bp.Length + 1)..] : "");
                if (rest.StartsWith("flex-row")) return "row";
                if (rest.StartsWith("flex-col")) return "column";
            }
        }
        return "row";
    }

    private static string? FloatClass(HtmlNode el)
    {
        foreach (var c in StyleLookup.ClassesOf(el))
        {
            string s = StripBp(c.ToLowerInvariant());
            if (s is "float-left" or "float-right") return s;
        }
        return null;
    }

    private static string StripBp(string c)
    {
        foreach (var bp in BpPriority)
            if (bp.Length > 0 && c.StartsWith(bp + ":")) return c[(bp.Length + 1)..];
        return c;
    }

    // ---------------- 7. card repetition ----------------

    private static ColDetect? CardRow(List<HtmlNode> kids)
    {
        string tag0 = kids[0].Name.ToLowerInvariant();
        if (tag0 is "li" or "a" or "span" or "option" or "tr" or "td" or "p") return null;
        if (kids.Any(k => !string.Equals(k.Name, kids[0].Name, StringComparison.OrdinalIgnoreCase)))
            return null;
        string cls0 = NormClass(kids[0]);
        if (kids.Any(k => NormClass(k) != cls0)) return null;
        if (kids.Any(k => BlockTextLength(k) < 20)) return null;
        int? rowSize = kids.Count <= 6 ? null : kids.Count <= 9 ? 3 : 4;
        return new ColDetect(Equal(kids.Count), null, "cards", rowSize);
    }

    private static string NormClass(HtmlNode node)
    {
        // "card-1" and "card-2" are the same component.
        var toks = StyleLookup.ClassesOf(node).Select(t =>
            Regex.Replace(t.ToLowerInvariant(), @"[-_]?\d+$", "")).ToList();
        toks.Sort(StringComparer.Ordinal);
        return string.Join(" ", toks);
    }

    private static int BlockTextLength(HtmlNode node)
    {
        var t = HtmlEntity.DeEntitize(node.InnerText ?? "");
        return Regex.Replace(t, @"\s+", " ").Trim().Length;
    }

    private static double[] Equal(int n)
    {
        var f = new double[n];
        Array.Fill(f, 1.0 / n);
        return f;
    }
}
