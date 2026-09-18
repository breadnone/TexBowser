using HtmlAgilityPack;

namespace TexBowser.Layout;

/// <summary>
/// Content sanitizing + visibility, applied BEFORE perception:
/// - Strip() removes everything that is never page content: scripts (inline JS
///   and JSON blobs, the classic "javascript leaking as text"), stylesheets,
///   noscript fallbacks, templates, media/embeds, iframes, svg art, metadata,
///   comments and the whole &lt;head&gt; (title + base-href + styles are read
///   out first). Removing — not just skipping — also protects every helper
///   that reads InnerText (headings, link labels, table cells, scores).
/// - IsHidden() filters display:none / visibility:hidden / hidden attribute /
///   aria-hidden subtrees (tab panels, modals, off-canvas menus, JS clones)
///   at every level: regions, segmentation, detectors, links and controls.
/// </summary>
public static class Sanitizer
{
    private static readonly string[] StripTags =
    {
        "script", "style", "noscript", "template", "link", "meta",
        "iframe", "embed", "object", "applet", "svg", "canvas",
        "video", "audio", "source", "track", "map", "area",
    };

    /// <summary>Remove non-content nodes in place. Call after title/styles/base extraction.</summary>
    public static void Strip(HtmlDocument doc)
    {
        var doomed = new List<HtmlNode>();
        foreach (var el in doc.DocumentNode.Descendants())
        {
            if (el.NodeType == HtmlNodeType.Comment) { doomed.Add(el); continue; }
            if (el.NodeType != HtmlNodeType.Element) continue;
            string tag = el.Name.ToLowerInvariant();
            if (tag == "head" || StripTags.Contains(tag)) doomed.Add(el);
        }
        foreach (var n in doomed)
        {
            try { n.Remove(); } catch { /* already detached */ }
        }
    }

    /// <summary>True when an element must not render (author-hidden or AT-hidden).</summary>
    public static bool IsHidden(HtmlNode el, StyleLookup? styles)
    {
        if (el.GetAttributeValue("hidden", (string?)null) != null) return true;
        if (el.GetAttributeValue("aria-hidden", "").Trim() == "true") return true;
        if (styles == null) return false;
        var eff = styles.Effective(el);
        string? disp = StyleLookup.Get(eff, "display");
        if (disp != null && disp.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("none"))
            return true;
        string? vis = StyleLookup.Get(eff, "visibility");
        if (vis is "hidden" or "collapse") return true;
        return false;
    }

    /// <summary>Any hidden ancestor (up to &lt;body&gt;) hides the node too.</summary>
    public static bool IsHiddenTree(HtmlNode el, StyleLookup? styles)
    {
        if (styles == null) return false;
        for (var n = el; n != null; n = n.ParentNode)
        {
            if (n.NodeType != HtmlNodeType.Element) continue;
            if (n.Name.Equals("body", StringComparison.OrdinalIgnoreCase)) return false;
            if (IsHidden(n, styles)) return true;
        }
        return false;
    }

    /// <summary>Page base URL honoring &lt;base href&gt; (call before Strip removes head).</summary>
    public static Uri? ResolveBase(HtmlDocument doc, string pageUrl)
    {
        try
        {
            Uri? page = Uri.TryCreate(pageUrl, UriKind.Absolute, out var u) ? u : null;
            var b = doc.DocumentNode.SelectSingleNode("//base[@href]");
            string? href = b?.GetAttributeValue("href", "").Trim();
            if (!string.IsNullOrEmpty(href))
            {
                if (page != null && Uri.TryCreate(page, href, out var abs)) return abs;
                if (Uri.TryCreate(href, UriKind.Absolute, out var abs2)) return abs2;
            }
            return page;
        }
        catch { return null; }
    }
}
