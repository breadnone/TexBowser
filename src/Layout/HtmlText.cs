using System.Text;
using HtmlAgilityPack;
using TexBowser.Render;

namespace TexBowser.Layout;

/// <summary>Shared HTML→text primitives: cleaning, URL resolving, inline collection.</summary>
public static class HtmlText
{
    public static string Clean(string? s) => TextConcat.Clean(s);

    public static string? Resolve(string href, Uri? baseUri)
    {
        href = (href ?? "").Trim();
        if (href.Length == 0) return null;
        if (href.StartsWith('#') || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            if (baseUri != null && Uri.TryCreate(baseUri, href, out var abs)) return abs.ToString();
            if (Uri.TryCreate(href, UriKind.Absolute, out var u2)) return u2.ToString();
        }
        catch { }
        return null;
    }

    /// <summary>Flatten inline content (links become "text [n]"), skipping tables/scripts.</summary>
    public static void CollectInline(HtmlNode n, LinkCollector links, Uri? baseUri, StringBuilder sb)
    {
        if (n.NodeType == HtmlNodeType.Text)
        {
            sb.Append(Clean(HtmlEntity.DeEntitize(((HtmlTextNode)n).Text ?? ""))).Append(' ');
            return;
        }
        if (n.NodeType != HtmlNodeType.Element) return;
        string tag = n.Name.ToLowerInvariant();
        if (LayoutPerceiver.SkipTags.Contains(tag) || tag == "table" || tag == "img") return;
        // Form controls render as live views; never as prose (no duplication).
        if (tag is "form" or "input" or "textarea" or "select" or "button"
            or "datalist" or "keygen" or "output") return;
        if (tag == "a")
        {
            string text = Clean(HtmlEntity.DeEntitize(n.InnerText ?? ""));
            string? href = Resolve(n.GetAttributeValue("href", ""), baseUri);
            if (href != null)
            {
                if (text.Length == 0) text = href;
                // Chained appends: int formats directly, no $"..." intermediate.
                sb.Append(text).Append(" [").Append(links.Add(text, href)).Append("] ");
            }
            else if (text.Length > 0) sb.Append(text).Append(' ');
            return;
        }
        if (tag == "br") { sb.Append('\n'); return; }
        foreach (var c in n.ChildNodes) CollectInline(c, links, baseUri, sb);
    }

    public static string CellText(HtmlNode cell, LinkCollector links, Uri? baseUri)
    {
        var sb = new StringBuilder();
        CollectInline(cell, links, baseUri, sb);
        return Clean(sb.ToString());
    }
}
