using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace TexBowser.Layout;

/// <summary>
/// Perceive step, detection-only (no learning):
/// sanitize → styles → scored regions → recursive segmentation → signature.
/// The terminal width is the viewport and is applied at render time.
/// </summary>
public static class LayoutPerceiver
{
    internal static readonly HashSet<string> SkipTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "svg", "canvas", "video", "audio", "iframe",
        "template", "input", "select", "textarea", "button", "form", "head", "meta", "link",
    };

    private static readonly HashSet<string> RegionTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "header", "nav", "main", "aside", "footer",
    };

    public static PerceivedPage Perceive(string html, string baseUrl)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html ?? "");

        var links = new LinkCollector();
        string title = HtmlText.Clean(doc.DocumentNode.SelectSingleNode("//title")?.InnerText ?? "");
        if (title.Length > 120) title = title[..120];

        var styles = StyleLookup.Parse(doc);
        Uri? baseUri = Sanitizer.ResolveBase(doc, baseUrl);
        Sanitizer.Strip(doc); // scripts, styles, media, comments, head — gone before perception

        var regions = RegionFinder.Find(doc, styles);
        var ctx = new SegCtx { Styles = styles, Links = links, Base = baseUri, Depth = 0 };

        // Every region becomes a layout tree (text AND forms/tables), so a search
        // box in the header or nav renders as live controls where the site has it.
        LayoutNode? headerTree = regions.Header != null ? Segmenter.Build(regions.Header, ctx) : null;
        if (headerTree != null && Segmenter.IsEmpty(headerTree)) headerTree = null;

        var navLinks = regions.Nav != null
            ? ExtractNavLinks(regions.Nav, links, baseUri, styles: styles) : new List<LinkInfo>();
        var navForms = new List<FormNode>();
        if (regions.Nav != null)
        {
            foreach (var f in regions.Nav.Descendants("form").Where(f => !HasAncestor(f, "form")))
            {
                var built = Segmenter.BuildForm(f, ctx.Child());
                if (built is FormNode fn && !Segmenter.IsEmpty(built)) navForms.Add(fn);
            }
        }

        LayoutNode mainTree = Segmenter.Build(regions.Main, ctx);

        var asideTrees = new List<LayoutNode>();
        foreach (var a in regions.Asides)
        {
            var built = Segmenter.Build(a, ctx.Child());
            if (!Segmenter.IsEmpty(built)) asideTrees.Add(built);
        }
        LayoutNode? footerTree = regions.Footer != null ? Segmenter.Build(regions.Footer, ctx) : null;
        if (footerTree != null && Segmenter.IsEmpty(footerTree)) footerTree = null;

        int mainCols = Math.Max(1, Segmenter.MaxRowCols(mainTree));
        var rowSources = Segmenter.RowSources(mainTree);
        var (asideRatio, asideRatioSource) = DetectAsideRatio(regions.Main, regions.Asides);

        bool hasHeader = headerTree != null;
        bool hasNav = navLinks.Count > 0 || navForms.Count > 0;
        bool hasAside = asideTrees.Count > 0;
        bool hasFooter = footerTree != null;

        string summary = $"M{mainCols}" + (rowSources.Count > 0 ? $" via {string.Join("+", rowSources)}" : " stacked")
                       + (hasAside ? $" · aside {asideRatio:P0} via {asideRatioSource}" : "");

        return new PerceivedPage(
            Title: title, BaseUrl: baseUrl,
            HasHeader: hasHeader, Header: headerTree,
            HasNav: hasNav, NavLinks: navLinks, NavForms: navForms,
            Main: mainTree, MainCols: mainCols,
            HasAside: hasAside, Asides: asideTrees,
            HasFooter: hasFooter, Footer: footerTree,
            AsideRatio: asideRatio, AsideRatioSource: asideRatioSource,
            RowSources: rowSources, DetectionSummary: summary,
            Links: links.Links);
    }

    private static bool HasAncestor(HtmlNode node, string tag)
    {
        for (var p = node.ParentNode; p != null; p = p.ParentNode)
            if (p.Name.Equals(tag, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // ---------- nav links ----------

    private static List<LinkInfo> ExtractNavLinks(HtmlNode nav, LinkCollector links, Uri? baseUri,
        StyleLookup? styles = null)
    {
        var result = new List<LinkInfo>();
        foreach (var a in nav.Descendants("a"))
        {
            if (styles != null && Sanitizer.IsHiddenTree(a, styles)) continue;
            if (HasAncestor(a, "form")) continue; // form links have no markers; skip numbering
            string text = HtmlText.Clean(HtmlEntity.DeEntitize(a.InnerText ?? ""));
            string? href = HtmlText.Resolve(a.GetAttributeValue("href", ""), baseUri);
            if (href == null || text.Length == 0) continue;
            int idx = links.Add(text, href);
            result.Add(new LinkInfo(idx, text, href));
            if (result.Count >= 40) break;
        }
        return result;
    }

    // ---------- section text ----------

    /// <summary>
    /// Flatten a subtree to heading + paragraphs. Descendant subtrees belonging
    /// to a *different* region, and &lt;table&gt; subtrees (rendered as real
    /// tables/rows by the segmenter), are skipped.
    /// </summary>
    public static SectionData ExtractSection(
        HtmlNode node, LinkCollector links, Uri? baseUri, string ownRegion = "",
        StyleLookup? styles = null)
    {
        string heading = "";
        if (node.Name.Length == 2 && node.Name.StartsWith("h", StringComparison.OrdinalIgnoreCase))
            heading = HtmlText.Clean(HtmlEntity.DeEntitize(node.InnerText ?? ""));
        else
        {
            var h = node.SelectSingleNode(".//h1|.//h2|.//h3");
            if (h != null) heading = HtmlText.Clean(HtmlEntity.DeEntitize(h.InnerText ?? ""));
        }
        if (heading.Length > 120) heading = heading[..120];

        var blocks = new List<string>();
        var cur = new StringBuilder();
        void Flush()
        {
            var t = HtmlText.Clean(cur.ToString());
            if (t.Length > 0) blocks.Add(t);
            cur.Clear();
        }

        void Walk(HtmlNode n)
        {
            if (n.NodeType == HtmlNodeType.Text)
            {
                cur.Append(HtmlText.Clean(HtmlEntity.DeEntitize(((HtmlTextNode)n).Text ?? ""))).Append(' ');
                return;
            }
            if (n.NodeType != HtmlNodeType.Element) return;
            string tag = n.Name.ToLowerInvariant();

            if (SkipTags.Contains(tag)) return;
            if (styles != null && Sanitizer.IsHidden(n, styles)) return;
            if (tag == "table") return;
            // Form controls render as live views; never as prose (no duplication).
            if (tag is "form" or "input" or "textarea" or "select" or "button"
                or "datalist" or "keygen" or "output") return;
            if (tag == "img") return; // images are never shown (text-only browser)
            if (RegionTags.Contains(tag) && !string.Equals(tag, ownRegion, StringComparison.OrdinalIgnoreCase))
                return;

            switch (tag)
            {
                case "br":
                    cur.Append('\n');
                    return;
                case "hr":
                    Flush();
                    return;
                case "a":
                {
                    string text = HtmlText.Clean(HtmlEntity.DeEntitize(n.InnerText ?? ""));
                    string? href = HtmlText.Resolve(n.GetAttributeValue("href", ""), baseUri);
                    if (href == null)
                    {
                        if (text.Length > 0) cur.Append(text).Append(' ');
                        return;
                    }
                    if (text.Length == 0) text = href;
                    int idx = links.Add(text, href);
                    cur.Append($"{text} [{idx}] ");
                    return;
                }
                case "li":
                    Flush();
                    var tmp = new StringBuilder();
                    HtmlText.CollectInline(n, links, baseUri, tmp);
                    string it = HtmlText.Clean(tmp.ToString());
                    if (it.Length > 0) blocks.Add("• " + it);
                    return;
                case "p" or "div" or "section" or "article" or "blockquote" or "pre"
                    or "h1" or "h2" or "h3" or "h4" or "h5" or "h6"
                    or "header" or "footer" or "nav" or "aside" or "main"
                    or "figure" or "figcaption" or "ul" or "ol":
                    Flush();
                    foreach (var c in n.ChildNodes) Walk(c);
                    Flush();
                    return;
                default:
                    foreach (var c in n.ChildNodes) Walk(c);
                    return;
            }
        }

        if (node.Name.Equals("a", StringComparison.OrdinalIgnoreCase)) Walk(node);
        else foreach (var c in node.ChildNodes) Walk(c);
        Flush();
        if (blocks.Count > 0 && heading.Length > 0)
        {
            // Heading links gain a " [n]" marker in prose ("News Stream [51]")
            // while Heading stays bare ("News Stream") — strip markers to dedup.
            string b0 = Regex.Replace(blocks[0], @"\s*\[\d+\]\s*$", "").Trim();
            if (b0 == heading)
            {
                heading = blocks[0];
                blocks.RemoveAt(0);
            }
        }

        return new SectionData(heading, blocks);
    }

    // ---------- main/sidebar split ratio (pure detection) ----------

    private static (double Ratio, string Source) DetectAsideRatio(HtmlNode mainNode, List<HtmlNode> asides)
    {
        const double Default = 0.70;
        var aside = asides.FirstOrDefault();
        if (aside == null) return (Default, "default");

        if (aside.ParentNode == mainNode.ParentNode && mainNode.ParentNode != null)
        {
            var r = SiblingRatio(mainNode.ParentNode, mainNode, aside);
            if (r != null) return (Clamp(r.Value), "grid");
        }
        for (var p = aside.ParentNode; p != null && Depth(p, aside) <= 5; p = p.ParentNode)
        {
            var kids = ElementChildren(p);
            if (kids.Count < 2 || kids.Count > 4) continue;
            HtmlNode? mainSide = kids.FirstOrDefault(k => k == mainNode || k.Descendants().Contains(mainNode));
            HtmlNode? asideSide = kids.FirstOrDefault(k => k == aside || k.Descendants().Contains(aside));
            if (mainSide == null || asideSide == null || mainSide == asideSide) continue;
            var r = SiblingRatio(p, mainSide, asideSide);
            if (r != null) return (Clamp(r.Value), "grid");
        }
        return (Default, "default");
    }

    private static double? SiblingRatio(HtmlNode parent, HtmlNode mainSide, HtmlNode asideSide)
    {
        var spans = new Dictionary<HtmlNode, int>();
        foreach (var k in ElementChildren(parent))
        {
            var m = Regex.Match(" " + k.GetAttributeValue("class", "") + " ",
                @"col-(?:(?:xs|sm|md|lg|xl|xxl)-)?(\d{1,2})\b|(?:^|\s)(?:small|medium|large)-(\d{1,2})(?:\s|$)|(?:^|\s)grid_(\d{1,2})(?:\s|$)",
                RegexOptions.IgnoreCase);
            int? span = null;
            for (int g = 1; g <= 3; g++)
                if (m.Groups[g].Success && int.TryParse(m.Groups[g].Value, out int s)) { span = s; break; }
            if (span != null) spans[k] = span.Value;
        }
        if (spans.TryGetValue(mainSide, out int ms) && spans.TryGetValue(asideSide, out int as_)
            && ms + as_ > 0)
            return ms / (double)(ms + as_);
        double? W(HtmlNode n)
        {
            var m = Regex.Match(n.GetAttributeValue("style", ""), @"width\s*:\s*([\d.]+)\s*%",
                RegexOptions.IgnoreCase);
            if (m.Success && double.TryParse(m.Groups[1].Value, out double v)) return v;
            var tw = ColumnDetectors.TailwindFrac(n);
            if (tw != null) return tw.Value * 100;
            return null;
        }
        var mw = W(mainSide);
        var aw = W(asideSide);
        if (mw != null && aw != null && mw + aw > 0) return mw.Value / (mw.Value + aw.Value);
        return null;
    }

    private static int Depth(HtmlNode ancestor, HtmlNode node)
    {
        int d = 0;
        for (var p = node; p != null && p != ancestor; p = p.ParentNode) d++;
        return d;
    }

    private static double Clamp(double r) => Math.Clamp(r, 0.5, 0.85);

    private static List<HtmlNode> ElementChildren(HtmlNode node) =>
        node.ChildNodes.Where(n => n.NodeType == HtmlNodeType.Element).ToList();
}
