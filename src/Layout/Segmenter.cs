using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace TexBowser.Layout;

/// <summary>
/// Turns a region subtree into a <see cref="LayoutNode"/> tree:
/// vertical stacks of text, horizontal column rows, and inline data tables.
/// Wrap-aware: Bootstrap spans overflowing 12, grid tracks with more children
/// than tracks, and repeated equal fractions all chunk into balanced visual
/// rows — mirroring how CSS wraps them on screen.
/// </summary>
public sealed class SegCtx
{
    public required StyleLookup Styles { get; init; }
    public required LinkCollector Links { get; init; }
    public required Uri? Base { get; init; }
    public int Depth { get; init; }
    public SegCtx Child() => new() { Styles = Styles, Links = Links, Base = Base, Depth = Depth + 1 };
}

public static class Segmenter
{
    private const int MaxDepth = 5;
    private const int MaxCellsPerRow = 6;

    private static readonly HashSet<string> LeafTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "h1", "h2", "h3", "h4", "h5", "h6", "li", "blockquote", "pre",
        "figcaption", "span", "a", "small", "em", "strong", "code", "label",
    };

    public static LayoutNode Build(HtmlNode node, SegCtx ctx)
    {
        string tag = node.Name.ToLowerInvariant();
        if (Sanitizer.IsHidden(node, ctx.Styles))
            return new TextNode(new SectionData("", new List<string>()));
        if (tag == "table") return BuildTable(node, ctx);
        if (tag == "form") return BuildForm(node, ctx);
        if (IsControlTag(tag)) return BuildAnonymousControl(node, ctx);

        var kids = ElementChildren(node);
        // Pure text leaf (text + inline elements only).
        if (kids.Count == 0 || (IsLeafTag(tag) && node.ChildNodes.All(IsInlineContent))
            || (tag == "a" && !node.Descendants().Any(n => n.Name is "table" or "form" || IsControlTag(n.Name))))
            return new TextNode(LayoutPerceiver.ExtractSection(node, ctx.Links, ctx.Base, ownRegion: "", styles: ctx.Styles));

        bool hasText = node.ChildNodes.Any(n => n.NodeType == HtmlNodeType.Text
            && !string.IsNullOrWhiteSpace(HtmlEntity.DeEntitize(n.InnerText)));
        var det = ctx.Depth < MaxDepth && !hasText && kids.Count >= 2
            ? ColumnDetectors.Detect(node, ctx.Styles) : null;
        if (det != null)
        {
            // Detection ignores hidden children — layout must use the same set.
            var visible = kids.Where(k => !Sanitizer.IsHidden(k, ctx.Styles)).ToList();
            if (visible.Count >= 2) return BuildRows(visible, det, ctx);
            if (visible.Count == 1) return Build(visible[0], ctx.Child());
            return new TextNode(new SectionData("", new List<string>()));
        }

        var parts = new List<LayoutNode>();
        var inline = node.OwnerDocument.CreateElement("div");
        void FlushInline()
        {
            if (!inline.HasChildNodes) return;
            var text = LayoutPerceiver.ExtractSection(inline, ctx.Links, ctx.Base, styles: ctx.Styles);
            if (!text.IsEmpty) parts.Add(new TextNode(text));
            inline.RemoveAllChildren();
        }
        foreach (var k in node.ChildNodes)
        {
            if (k.NodeType == HtmlNodeType.Comment) continue;
            if (k.NodeType == HtmlNodeType.Element && Sanitizer.IsHidden(k, ctx.Styles)) continue;
            if (IsInlineContent(k))
            {
                inline.AppendChild(k.CloneNode(true));
                continue;
            }
            FlushInline();
            var built = Build(k, ctx.Child());
            if (!IsEmpty(built)) parts.Add(built);
        }
        FlushInline();
        if (parts.Count == 0)
            return new TextNode(new SectionData("", new List<string>()));
        if (parts.Count == 1) return parts[0]; // transparent wrapper
        return new StackNode(parts);
    }

    private static bool IsInlineContent(HtmlNode node)
    {
        if (node.NodeType is HtmlNodeType.Text or HtmlNodeType.Comment) return true;
        if (node.NodeType != HtmlNodeType.Element) return false;
        string tag = node.Name.ToLowerInvariant();
        return tag is ("span" or "a" or "small" or "em" or "strong" or "code" or "label"
            or "b" or "i" or "u" or "s" or "del" or "ins" or "sub" or "sup" or "abbr"
            or "cite" or "q" or "time" or "mark" or "kbd" or "samp" or "var" or "br"
            or "wbr" or "img") && node.ChildNodes.All(IsInlineContent);
    }

    private static bool IsLeafTag(string tag) => LeafTags.Contains(tag);

    private static bool IsControlTag(string tag) => tag is "input" or "textarea" or "select" or "button";

    private static List<HtmlNode> ElementChildren(HtmlNode node) =>
        node.ChildNodes.Where(n => n.NodeType == HtmlNodeType.Element).ToList();

    // ---------------- forms ----------------

    /// <summary>One stray control outside any form: still live, submits nowhere.</summary>
    private static LayoutNode BuildAnonymousControl(HtmlNode node, SegCtx ctx)
    {
        var tmp = new List<FormControl>();
        CollectControl(node, tmp, new Dictionary<string, string>(), ctx);
        if (tmp.Count == 0)
            return new TextNode(LayoutPerceiver.ExtractSection(node, ctx.Links, ctx.Base, ownRegion: "", styles: ctx.Styles));
        return new FormNode(new FormData("", "get", "", "", tmp));
    }

    public static LayoutNode BuildForm(HtmlNode form, SegCtx ctx)
    {
        string action = form.GetAttributeValue("action", "").Trim();
        string method = form.GetAttributeValue("method", "get").Trim().ToLowerInvariant();
        if (method is not ("get" or "post")) method = "get";
        string enctype = form.GetAttributeValue("enctype", "").Trim().ToLowerInvariant();
        string title = "";
        var legend = form.SelectSingleNode(".//legend");
        if (legend != null) title = HtmlText.Clean(HtmlEntity.DeEntitize(legend.InnerText ?? ""));

        // <label for="id"> → prompt text.
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var lab in form.SelectNodes(".//label") ?? Enumerable.Empty<HtmlNode>())
        {
            string f = lab.GetAttributeValue("for", "");
            if (f.Length == 0) continue;
            string t = HtmlText.Clean(HtmlEntity.DeEntitize(lab.InnerText ?? ""));
            if (t.Length > 0 && !labels.ContainsKey(f)) labels[f] = t;
        }

        var controls = new List<FormControl>();
        foreach (var el in form.Descendants().Where(n => n.NodeType == HtmlNodeType.Element))
        {
            string tag = el.Name.ToLowerInvariant();
            if (tag is "input" or "textarea" or "select" or "button")
                CollectControl(el, controls, labels, ctx);
        }
        return new FormNode(new FormData(action, method, enctype, title, controls));
    }

    private static void CollectControl(HtmlNode el, List<FormControl> controls,
        Dictionary<string, string> labels, SegCtx ctx)
    {
        if (Sanitizer.IsHidden(el, ctx.Styles)) return; // hidden inputs render nothing
        if (el.GetAttributeValue("hidden", (string?)null) != null
            && !el.Name.Equals("input", StringComparison.OrdinalIgnoreCase)) return;
        string tag = el.Name.ToLowerInvariant();
        string id = el.GetAttributeValue("id", "");
        string inputType = tag == "input" ? el.GetAttributeValue("type", "text").Trim().ToLowerInvariant() : "";
        if (inputType == "") inputType = "text";
        bool disabled = el.GetAttributeValue("disabled", (string?)null) != null;
        bool readOnly = el.GetAttributeValue("readonly", (string?)null) != null;

        string Prompt(string name, string placeholder, string fallback)
        {
            if (id.Length > 0 && labels.TryGetValue(id, out var lt)) return Trunc(lt, 60);
            for (var p = el.ParentNode; p != null; p = p.ParentNode)
            {
                if (p.Name.Equals("label", StringComparison.OrdinalIgnoreCase))
                {
                    string t = HtmlText.Clean(HtmlEntity.DeEntitize(p.InnerText ?? ""));
                    // A wrapping label's text includes the control itself
                    // ("Format [select+options]") — subtract it to get the prompt.
                    string own = HtmlText.Clean(HtmlEntity.DeEntitize(el.InnerText ?? ""));
                    if (own.Length > 0 && t.Length > 0)
                        t = HtmlText.Clean(t.Replace(own, " "));
                    if (t.Length > 0) return Trunc(t, 60);
                    break;
                }
                if (p.Name.Equals("form", StringComparison.OrdinalIgnoreCase)) break;
            }
            // Checkboxes/radios are conventionally labeled by adjacent bare text
            // ("<input> Low"), which belongs to the control, not the prose.
            if (tag == "input" && inputType is "checkbox" or "radio")
            {
                string adj = AdjacentText(el);
                if (adj.Length > 0) return Trunc(adj, 60);
            }
            if (placeholder.Length > 0) return Trunc(placeholder, 60);
            if (name.Length > 0) return name;
            return fallback;
        }

        switch (tag)
        {
            case "input":
            {
                if (inputType is "text" or "password" or "search" or "url" or "email" or "tel"
                    or "number" or "hidden" or "file")
                {
                    string name = el.GetAttributeValue("name", "");
                    string val = el.GetAttributeValue("value", "");
                    string ph = el.GetAttributeValue("placeholder", "");
                    controls.Add(new FormControl(inputType, name, val, ph,
                        Prompt(name, ph, inputType), false, disabled || (readOnly && inputType != "hidden"), new()));
                }
                else if (inputType is "checkbox" or "radio")
                {
                    string name = el.GetAttributeValue("name", "");
                    string val = el.GetAttributeValue("value", inputType == "checkbox" ? "on" : "");
                    bool chk = el.GetAttributeValue("checked", (string?)null) != null;
                    controls.Add(new FormControl(inputType, name, val, "",
                        Prompt(name, val, inputType), chk, disabled, new()));
                }
                else if (inputType is "submit" or "button" or "reset" or "image")
                {
                    string name = el.GetAttributeValue("name", "");
                    string val = el.GetAttributeValue("value", "");
                    string alt = el.GetAttributeValue("alt", "");
                    string label = val.Length > 0 ? val : alt.Length > 0 ? alt
                        : inputType == "submit" ? "Submit" : inputType == "reset" ? "Reset" : "Button";
                    controls.Add(new FormControl(inputType, name, val, "", label, false, disabled, new()));
                }
                else
                {
                    // color/date/range/... degrade to plain text entry.
                    string name = el.GetAttributeValue("name", "");
                    controls.Add(new FormControl("text", name, el.GetAttributeValue("value", ""),
                        el.GetAttributeValue("placeholder", ""), Prompt(name, "", inputType), false, disabled, new()));
                }
                break;
            }
            case "textarea":
            {
                string name = el.GetAttributeValue("name", "");
                string val = HtmlText.Clean(HtmlEntity.DeEntitize(el.InnerText ?? ""));
                string ph = el.GetAttributeValue("placeholder", "");
                controls.Add(new FormControl("textarea", name, val, ph,
                    Prompt(name, ph, "text"), false, disabled || readOnly, new()));
                break;
            }
            case "select":
            {
                string name = el.GetAttributeValue("name", "");
                var options = new List<FormOption>();
                foreach (var opt in el.SelectNodes(".//option") ?? Enumerable.Empty<HtmlNode>())
                {
                    string inner = HtmlText.Clean(HtmlEntity.DeEntitize(opt.InnerText ?? ""));
                    string v = opt.GetAttributeValue("value", inner);
                    string t = inner.Length > 0 ? inner : v;
                    bool sel = opt.GetAttributeValue("selected", (string?)null) != null;
                    options.Add(new FormOption(v, t.Length > 60 ? t[..60] : t, sel));
                }
                if (options.Count == 0) break;
                if (options.All(o => !o.Selected))
                    options[0] = options[0] with { Selected = true };
                controls.Add(new FormControl("select", name, "", "",
                    Prompt(name, "", "choose"), false, disabled, options));
                break;
            }
            case "button":
            {
                string btype = el.GetAttributeValue("type", "submit").Trim().ToLowerInvariant();
                if (btype is not ("submit" or "reset" or "button")) btype = "submit";
                string label = HtmlText.Clean(HtmlEntity.DeEntitize(el.InnerText ?? ""));
                if (label.Length == 0) label = btype == "submit" ? "Submit" : btype == "reset" ? "Reset" : "Button";
                if (label.Length > 40) label = label[..40];
                controls.Add(new FormControl(btype, el.GetAttributeValue("name", ""),
                    el.GetAttributeValue("value", ""), "", label, false, disabled, new()));
                break;
            }
        }
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n];

    /// <summary>Nearest bare text beside a control (following first, then preceding).</summary>
    private static string AdjacentText(HtmlNode el)
    {
        var sib = el.NextSibling;
        while (sib != null)
        {
            if (sib.NodeType == HtmlNodeType.Text)
            {
                string t = HtmlText.Clean(HtmlEntity.DeEntitize(((HtmlTextNode)sib).Text ?? ""));
                if (t.Length > 0) return t;
            }
            else if (sib.NodeType != HtmlNodeType.Element
                || (sib.Name is not ("br" or "wbr") && sib.InnerText.Trim().Length > 0))
            {
                break;
            }
            sib = sib.NextSibling;
        }
        sib = el.PreviousSibling;
        while (sib != null)
        {
            if (sib.NodeType == HtmlNodeType.Text)
            {
                string t = HtmlText.Clean(HtmlEntity.DeEntitize(((HtmlTextNode)sib).Text ?? ""));
                if (t.Length > 0) return t;
            }
            else if (sib.NodeType != HtmlNodeType.Element
                || (sib.Name is not ("br" or "wbr") && sib.InnerText.Trim().Length > 0))
            {
                break;
            }
            sib = sib.PreviousSibling;
        }
        return "";
    }

    // ---------------- rows + wrap chunking ----------------

    private static LayoutNode BuildRows(List<HtmlNode> kids, ColDetect det, SegCtx ctx)
    {
        var chunks = Chunk(det, kids.Count);
        var rows = new List<LayoutNode>();
        int at = 0;
        foreach (int size in chunks)
        {
            var cells = new List<CellNode>();
            double[] slice = Slice(det.Fracs, at, size);
            double sum = slice.Sum();
            for (int i = 0; i < size; i++)
            {
                var content = Build(kids[at + i], ctx.Child());
                if (IsEmpty(content))
                    content = new TextNode(new SectionData("", new List<string>()));
                cells.Add(new CellNode(content, sum > 0 ? slice[i] / sum : 1.0 / size));
            }
            at += size;
            if (cells.Count > 0 && cells.Any(c => !IsEmpty(c.Content))) rows.Add(new RowNode(cells, det.Source));
        }
        if (rows.Count == 0)
            return new TextNode(LayoutPerceiver.ExtractSection(kids[0], ctx.Links, ctx.Base, ownRegion: "", styles: ctx.Styles));
        if (rows.Count == 1) return rows[0];
        return new StackNode(rows);
    }

    private static double[] Slice(double[] fracs, int at, int size)
    {
        var s = new double[size];
        for (int i = 0; i < size; i++)
            s[i] = at + i < fracs.Length ? fracs[at + i] : fracs[^1];
        return s;
    }

    internal static List<int> Chunk(ColDetect det, int n)
    {
        // Bootstrap-style span overflow → greedy rows that fit 12.
        if (det.Spans != null && det.Spans.Sum() > 12)
        {
            var rows = new List<int>();
            int cur = 0, curSum = 0;
            for (int i = 0; i < n; i++)
            {
                int span = i < det.Spans.Length ? det.Spans[i] : 12;
                if (cur > 0 && curSum + span > 12) { rows.Add(cur); cur = 0; curSum = 0; }
                cur++;
                curSum += span;
            }
            if (cur > 0) rows.Add(cur);
            return CapRowSizes(rows);
        }
        // Known track count with more children → balanced visual rows.
        if (det.RowSize is { } m && n > m)
            return CapRowSizes(Balanced(n, m));
        // Repeated equal fractions that overflow one row → balanced rows.
        // (m == 1 means full-width items: genuinely stacked.)
        if (det.Spans == null && det.Fracs.Length > 0)
        {
            double f0 = det.Fracs[0];
            if (det.Fracs.All(f => Math.Abs(f - f0) < 1e-9) && n * f0 > 1.05)
            {
                int m2 = (int)Math.Round(1 / f0);
                m2 = Math.Clamp(m2, 1, 6);
                if (m2 >= n) return new List<int> { n };
                if (m2 == 1) return Enumerable.Repeat(1, n).ToList();
                return CapRowSizes(Balanced(n, m2));
            }
        }
        return new List<int> { n };
    }

    private static List<int> Balanced(int n, int target)
    {
        int rows = Math.Max(1, (n + target - 1) / target);
        int baseSize = n / rows, rem = n % rows;
        var out_ = new List<int>();
        for (int r = 0; r < rows; r++) out_.Add(baseSize + (r < rem ? 1 : 0));
        return out_;
    }

    private static List<int> CapRowSizes(List<int> rows)
    {
        // No text column row wider than MaxCellsPerRow stays readable.
        var capped = new List<int>();
        foreach (int size in rows)
        {
            if (size <= MaxCellsPerRow) capped.Add(size);
            else capped.AddRange(Balanced(size, MaxCellsPerRow));
        }
        return capped;
    }

    internal static bool IsEmpty(LayoutNode node) => node switch
    {
        TextNode t => t.Text.IsEmpty,
        DataTableNode d => d.Table.Headers.Count == 0 && d.Table.Rows.Count == 0,
        RowNode r => r.Cells.Count == 0 || r.Cells.All(c => IsEmpty(c.Content)),
        StackNode s => s.Children.Count == 0 || s.Children.All(IsEmpty),
        FormNode f => f.Form.Controls.Count == 0,
        _ => false,
    };

    // ---------------- tables: data vs layout ----------------

    private static LayoutNode BuildTable(HtmlNode table, SegCtx ctx)
    {
        var parsed = TableParser.Parse(table, ctx.Links, ctx.Base);
        if (IsDataTable(table, parsed)) return new DataTableNode(parsed);

        // Layout table → column row(s) from its rows.
        var trs = DirectRows(table);
        if (trs.Count == 0)
            return new TextNode(LayoutPerceiver.ExtractSection(table, ctx.Links, ctx.Base, ownRegion: "", styles: ctx.Styles));
        var rows = new List<LayoutNode>();
        foreach (var tr in trs)
        {
            var tds = tr.ChildNodes
                .Where(n => n.NodeType == HtmlNodeType.Element
                    && (n.Name.Equals("td", StringComparison.OrdinalIgnoreCase)
                        || n.Name.Equals("th", StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (tds.Count == 0) continue;
            var spans = tds.Select(td =>
            {
                int cs = td.GetAttributeValue("colspan", 1);
                return Math.Clamp(cs, 1, 12);
            }).ToArray();
            double total = spans.Sum();
            var cells = tds.Select((td, i) =>
            {
                var content = Build(td, ctx.Child());
                if (IsEmpty(content))
                    content = new TextNode(new SectionData("", new List<string>()));
                return new CellNode(content, spans[i] / total);
            }).ToList();
            // Wide layout rows chunk into readable groups.
            while (cells.Count > 0)
            {
                var group = cells.Take(MaxCellsPerRow).ToList();
                cells.RemoveRange(0, group.Count);
                double gsum = group.Sum(c => c.Fraction);
                rows.Add(new RowNode(
                    group.Select(c => c with { Fraction = c.Fraction / gsum }).ToList(), "table"));
            }
        }
        if (rows.Count == 0)
            return new TextNode(LayoutPerceiver.ExtractSection(table, ctx.Links, ctx.Base, ownRegion: "", styles: ctx.Styles));
        if (rows.Count == 1) return rows[0];
        return new StackNode(rows);
    }

    private static List<HtmlNode> DirectRows(HtmlNode table)
    {
        var direct = table.ChildNodes
            .Where(n => n.NodeType == HtmlNodeType.Element
                && n.Name.Equals("tr", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (direct.Count > 0) return direct;
        var rows = new List<HtmlNode>();
        foreach (var section in table.ChildNodes.Where(n => n.NodeType == HtmlNodeType.Element))
        {
            if (section.Name.Equals("thead", StringComparison.OrdinalIgnoreCase)
                || section.Name.Equals("tbody", StringComparison.OrdinalIgnoreCase)
                || section.Name.Equals("tfoot", StringComparison.OrdinalIgnoreCase))
                rows.AddRange(section.ChildNodes.Where(n => n.NodeType == HtmlNodeType.Element
                    && n.Name.Equals("tr", StringComparison.OrdinalIgnoreCase)));
        }
        return rows;
    }

    internal static bool IsDataTable(HtmlNode table, DataTable parsed)
    {
        if (parsed.Headers.Count > 0) return true; // <th> = data, the classic signal
        if (parsed.Rows.Count < 2) return false;   // single-row tables are layout
        int cols = parsed.Rows[0].Count;
        if (cols < 2) return false;
        if (parsed.Rows.Any(r => r.Count != cols)) return false;
        double avgLen = parsed.Rows.SelectMany(r => r).Average(c => c.Length);
        if (avgLen > 150) return false; // prose blocks, not records
        // Block-level content inside cells (divs, lists, nested tables) = layout.
        foreach (var td in table.SelectNodes(".//td|.//th") ?? Enumerable.Empty<HtmlNode>())
        {
            foreach (var inner in td.Descendants())
            {
                if (inner.NodeType != HtmlNodeType.Element) continue;
                string t = inner.Name.ToLowerInvariant();
                if (t is "div" or "p" or "ul" or "ol" or "table" or "section" or "article"
                    or "h1" or "h2" or "h3" or "h4" or "header" or "footer" or "nav" or "aside")
                    return false;
            }
        }
        return true;
    }

    // ---------------- tree walks ----------------

    public static int MaxRowCols(LayoutNode node) => node switch
    {
        RowNode r => Math.Max(r.Cells.Count,
            r.Cells.Count == 0 ? 0 : r.Cells.Max(c => MaxRowCols(c.Content))),
        StackNode s => s.Children.Count == 0 ? 0 : s.Children.Max(MaxRowCols),
        _ => 0,
    };

    public static List<string> RowSources(LayoutNode node)
    {
        var seen = new List<string>();
        void Walk(LayoutNode n)
        {
            switch (n)
            {
                case RowNode r:
                    if (!seen.Contains(r.Source)) seen.Add(r.Source);
                    foreach (var c in r.Cells) Walk(c.Content);
                    break;
                case StackNode s:
                    foreach (var c in s.Children) Walk(c);
                    break;
            }
        }
        Walk(node);
        return seen;
    }
}
