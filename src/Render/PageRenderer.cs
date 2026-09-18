using TexBowser.Layout;

namespace TexBowser.Render;

/// <summary>
/// Reconstruct step: the detected layout tree becomes text grids.
/// Column rows render side by side with pixel-exact widths (largest remainder);
/// main/aside split at the detected ratio, stacking below 84 cols; rows whose
/// columns would go under 14 cells stack with separators; nav is a horizontal
/// strip; data tables render bordered, inline, where they appear.
/// </summary>
public static class PageRenderer
{
    /// <summary>Below this viewport width the sidebar stacks instead of floating.</summary>
    public const int CollapseBelow = 84;

    /// <summary>Narrowest acceptable text column before a row falls back to stacked.</summary>
    public const int MinColWidth = 14;

    public static RenderedPage Render(PerceivedPage page, string url, int viewport)
    {
        int w = Math.Clamp(viewport, 40, 300);
        var lines = new List<string>();

        string host;
        try
        {
            host = new Uri(url).Host;
            if (host.Length == 0) host = url; // e.g. about:demo
        }
        catch { host = url; }

        lines.Add(new string('═', w));
        lines.AddRange(TextUtil.Wrap(TextConcat.HeaderLine(host, page.Signature, page.DetectionSummary, w), w));
        if (!string.IsNullOrWhiteSpace(page.Title))
            lines.AddRange(TextUtil.Wrap(page.Title, w));
        lines.Add(new string('═', w));

        if (page.HasHeader && page.Header != null)
        {
            lines.Add(TextUtil.Rule(w));
            lines.AddRange(RenderNode(page.Header, w));
        }

        if (page.HasNav)
        {
            bool anyNav = false;
            if (page.NavLinks.Count > 0)
            {
                lines.Add(TextUtil.Rule(w));
                var items = page.NavLinks.Select(l => TextConcat.LinkLabel(l.Text, l.Index)).ToList();
                lines.AddRange(TextUtil.WrapStrip(items, w));
                anyNav = true;
            }
            foreach (var f in page.NavForms)
            {
                if (!anyNav) { lines.Add(TextUtil.Rule(w)); anyNav = true; }
                lines.AddRange(RenderNode(f, w));
            }
        }

        bool sideBySide = page.HasAside && w >= CollapseBelow;
        if (sideBySide)
        {
            int mainW = (int)Math.Round((w - TextUtil.ColSep.Length) * page.AsideRatio);
            mainW = Math.Clamp(mainW, 20, w - 20 - TextUtil.ColSep.Length);
            int asideW = w - TextUtil.ColSep.Length - mainW;
            var mainLines = RenderNode(page.Main, mainW);
            var asideLines = new List<string>();
            for (int i = 0; i < page.Asides.Count; i++)
            {
                if (i > 0) asideLines.Add("╌╌╌");
                asideLines.AddRange(RenderNode(page.Asides[i], asideW));
            }
            lines.Add(TextUtil.Rule(w));
            lines.AddRange(TextUtil.RenderColumnGrid(
                new[] { (IReadOnlyList<string>)mainLines, asideLines },
                new[] { mainW, asideW }));
        }
        else
        {
            var mainLines = RenderNode(page.Main, w);
            if (mainLines.Count > 0)
            {
                lines.Add(TextUtil.Rule(w));
                lines.AddRange(mainLines);
            }
            if (page.HasAside)
            {
                for (int i = 0; i < page.Asides.Count; i++)
                {
                    lines.Add(TextUtil.Rule(w));
                    lines.AddRange(RenderNode(page.Asides[i], w));
                }
            }
        }

        if (page.HasFooter && page.Footer != null)
        {
            lines.Add(TextUtil.Rule(w));
            lines.AddRange(RenderNode(page.Footer, w));
        }

        if (page.Links.Count > 0)
        {
            lines.Add(TextUtil.Rule(w, "links"));
            foreach (var l in page.Links)
                lines.Add(TextConcat.LinkIndexLine(l.Index, l.Text, l.Url, w));
        }

        lines.Add(new string('═', w));
        return new RenderedPage(url, page.Title, page.Signature, page.DetectionSummary, lines, page.Links, page);
    }

    internal static List<string> RenderNode(LayoutNode node, int width)
    {
        return node switch
        {
            TextNode t => BlockLines(t.Text, width),
            DataTableNode d => RenderDataTableNode(d, width),
            RowNode r => RenderRow(r, width),
            StackNode s => RenderStack(s, width),
            FormNode f => RenderFormText(f.Form, width),
            _ => new List<string>(),
        };
    }

    private static List<string> RenderStack(StackNode stack, int width)
    {
        var lines = new List<string>();
        foreach (var child in stack.Children)
        {
            var childLines = RenderNode(child, width);
            if (childLines.Count == 0) continue;
            if (lines.Count > 0) lines.Add("");
            lines.AddRange(childLines);
        }
        return lines;
    }

    private static List<string> RenderRow(RowNode row, int width)
    {
        var fracs = row.Cells.Select(c => c.Fraction).ToArray();
        double sum = fracs.Sum();
        if (sum <= 0) fracs = fracs.Select(_ => 1.0 / fracs.Length).ToArray();
        else fracs = fracs.Select(f => f / sum).ToArray();

        int[] widths = TextUtil.PixelWidths(fracs, width - TextUtil.ColSep.Length * (fracs.Length - 1));
        if (widths.Any(x => x < MinColWidth) || widths.Sum() <= 0)
        {
            var stacked = new List<string>();
            for (int i = 0; i < row.Cells.Count; i++)
            {
                stacked.Add(TextUtil.Rule(width, TextConcat.ColLabel(i, row.Cells.Count)));
                stacked.AddRange(RenderNode(row.Cells[i].Content, width));
            }
            return stacked;
        }

        var cols = new List<IReadOnlyList<string>>();
        for (int i = 0; i < row.Cells.Count; i++)
            cols.Add(RenderNode(row.Cells[i].Content, widths[i]));
        return TextUtil.RenderColumnGrid(cols, widths);
    }

    private static List<string> RenderDataTableNode(DataTableNode node, int width)
    {
        var t = node.Table;
        int cols = Math.Max(t.Headers.Count, t.Rows.Count == 0 ? 0 : t.Rows.Max(r => r.Count));
        var lines = new List<string>
        {
            TextUtil.Rule(width, TextConcat.TableTitle(t.Rows.Count, cols)),
        };
        lines.AddRange(TextUtil.RenderDataTable(t.Headers, t.Rows, width));
        return lines;
    }

    /// <summary>
    /// Headless (--dump) form rendering: honest field descriptors.
    /// In the TUI these same controls are live views (see ViewRenderer).
    /// </summary>
    internal static List<string> RenderFormText(FormData form, int width)
    {
        var lines = new List<string>();
        lines.Add(TextUtil.Rule(width, TextConcat.FormHead(form.Title, form.Method, form.Action)));
        var seenRadio = new HashSet<string>();
        foreach (var c in form.Controls)
        {
            if (c.Kind == "hidden") continue;
            if (c.Kind == "radio")
            {
                if (!seenRadio.Add(c.Name)) continue;
                var group = form.Controls.Where(g => g.Kind == "radio" && g.Name == c.Name).ToList();
                var distinct = group.Select(g => g.Label)
                    .Where(l => l.Length > 0 && l != "radio").Distinct().ToList();
                if (distinct.Count == 1)
                    lines.AddRange(TextUtil.Wrap(TextConcat.WithColon(distinct[0]), width));
                foreach (var g in group)
                    lines.AddRange(TextUtil.Wrap(
                        TextConcat.RadioLine(g.Checked, g.Label.Length > 0 ? g.Label : g.Value), width));
                continue;
            }
            switch (c.Kind)
            {
                case "checkbox":
                    lines.AddRange(TextUtil.Wrap(
                        TextConcat.CheckLine(c.Checked, c.Label.Length > 0 ? c.Label : c.Name), width));
                    break;
                case "select":
                    lines.AddRange(TextUtil.Wrap(TextConcat.WithColon(c.Label), width));
                    foreach (var o in c.Options)
                        lines.AddRange(TextUtil.Wrap(
                            TextConcat.OptionLine(o.Selected, o.Text), width));
                    break;
                case "textarea":
                    lines.AddRange(TextUtil.Wrap(TextConcat.WithColon(c.Label), width));
                    lines.AddRange(TextUtil.Wrap(c.Value.Length > 0 ? c.Value : "…", width));
                    break;
                case "submit" or "button" or "reset" or "image":
                    lines.AddRange(TextUtil.Wrap(TextConcat.BracketSpaced(c.Label), width));
                    break;
                case "file":
                    lines.AddRange(TextUtil.Wrap(
                        TextConcat.FileLine(c.Label, c.Value.Length > 0 ? c.Value : "…"), width));
                    break;
                default:
                    string show = c.Kind == "password" ? new string('•', Math.Min(c.Value.Length, 16)) : c.Value;
                    lines.AddRange(TextUtil.Wrap(
                        TextConcat.FieldLine(c.Label, c.Kind, show), width));
                    break;
            }
        }
        return lines;
    }

    internal static List<string> BlockLines(SectionData block, int width, string? label = null)
    {
        var lines = new List<string>();
        if (!string.IsNullOrEmpty(label)) lines.Add(TextConcat.Bracketed(label));
        if (!string.IsNullOrEmpty(block.Heading))
        {
            var hw = TextUtil.Wrap(block.Heading, width);
            lines.AddRange(hw);
            if (hw.Count > 0)
                lines.Add(new string('─', Math.Min(Math.Max(hw.Max(l => l.Length), 3), width)));
        }
        lines.AddRange(TextUtil.WrapAll(block.Paragraphs, width));
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static string FitLine(string s, int w) => TextConcat.FitLine(s, w);

    private static string Trunc(string s, int n) => TextConcat.Trunc(s, n);
}
