using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace TexBowser.Layout;

/// <summary>
/// Scored region assignment. Tags and ARIA roles are strong signals; id/class
/// keywords are weak signals resolved by score (id beats class, early document
/// position favors header/nav, text volume favors main). Explicit claims always
/// beat keyword guesses, so e.g. &lt;nav class="main-nav"&gt; can never be
/// misfiled as "main". Multiple asides merge; unhooked pages fall back to the
/// largest content block.
/// </summary>
public sealed record Regions(
    HtmlNode? Header,
    HtmlNode? Nav,
    HtmlNode Main,
    List<HtmlNode> Asides,
    HtmlNode? Footer);

public static class RegionFinder
{
    private static readonly string[] HeaderKws = { "masthead", "site-header", "page-header", "topbar", "top-bar", "header" };
    private static readonly string[] NavKws = { "navbar", "nav", "menu", "topnav", "main-nav", "site-nav", "breadcrumb" };
    private static readonly string[] MainKws = { "content", "main", "post-content", "entry-content", "article-body", "page-body", "post", "article" };
    private static readonly string[] AsideKws = { "sidebar", "side-bar", "aside", "widget", "secondary", "rail" };
    private static readonly string[] FooterKws = { "footer", "site-footer", "page-footer", "colophon" };

    private sealed record Candidate(HtmlNode Node, string Kind, int Strength, int MatchLevel, int Order);

    public static Regions Find(HtmlDocument doc, StyleLookup? styles = null)
    {
        var cands = new List<Candidate>();
        int order = 0;
        foreach (var el in doc.DocumentNode.Descendants().Where(n => n.NodeType == HtmlNodeType.Element))
        {
            string tag = el.Name.ToLowerInvariant();
            if (tag is "html" or "body" or "head") continue;
            if (styles != null && Sanitizer.IsHidden(el, styles)) continue;
            string role = el.GetAttributeValue("role", "").ToLowerInvariant();
            string id = el.GetAttributeValue("id", "").ToLowerInvariant();
            string cls = el.GetAttributeValue("class", "").ToLowerInvariant();

            // Strength 3: explicit tag or ARIA role.
            if (tag == "header" || role == "banner") cands.Add(new(el, "header", 3, 0, order));
            else if (tag == "nav" || role == "navigation") cands.Add(new(el, "nav", 3, 0, order));
            else if (tag == "main" || role == "main") cands.Add(new(el, "main", 3, 0, order));
            else if (tag == "aside" || role == "complementary") cands.Add(new(el, "aside", 3, 0, order));
            else if (tag == "footer" || role == "contentinfo") cands.Add(new(el, "footer", 3, 0, order));
            else
            {
                // Strength by match level: id hit (2) beats class hit (1).
                var (kind, level) = KeywordHit(id, cls);
                if (kind != null) cands.Add(new(el, kind, level, level, order));
            }
            order++;
        }

        var claimed = new HashSet<HtmlNode>();
        HtmlNode? header = TakeBest(cands, "header", claimed);
        HtmlNode? nav = TakeBest(cands, "nav", claimed);
        HtmlNode? main = TakeBest(cands, "main", claimed);
        HtmlNode? footer = TakeBest(cands, "footer", claimed);
        var asides = TakeAllAsides(cands, claimed, header, nav, footer);

        main ??= FallbackMain(doc, claimed) ?? doc.DocumentNode.SelectSingleNode("//body") ?? doc.DocumentNode;

        return new Regions(header, nav, main, asides, footer);
    }

    private static (string? Kind, int Level) KeywordHit(string id, string cls)
    {
        var idTokens = Tokens(id);
        var clsTokens = Tokens(cls);
        string? idKind = KindFor(idTokens);
        if (idKind != null) return (idKind, 2);
        string? clsKind = KindFor(clsTokens);
        if (clsKind != null) return (clsKind, 1);
        return (null, 0);
    }

    private static HashSet<string> Tokens(string s) =>
        Regex.Split(s ?? "", @"[^a-z0-9]+").Where(t => t.Length > 0).ToHashSet();

    private static string? KindFor(HashSet<string> tokens)
    {
        // Fixed priority mirrors document anatomy: header → nav → main → aside → footer.
        if (HeaderKws.Any(tokens.Contains)) return "header";
        if (NavKws.Any(tokens.Contains)) return "nav";
        if (MainKws.Any(tokens.Contains)) return "main";
        if (AsideKws.Any(tokens.Contains)) return "aside";
        if (FooterKws.Any(tokens.Contains)) return "footer";
        return null;
    }

    private static HtmlNode? TakeBest(List<Candidate> cands, string kind, HashSet<HtmlNode> claimed)
    {
        foreach (var c in cands
                     .Where(c => c.Kind == kind && !claimed.Contains(c.Node))
                     .OrderByDescending(c => c.Strength)
                     .ThenByDescending(c => c.MatchLevel)
                     .ThenBy(c => kind == "footer" ? -c.Order : c.Order))
        {
            claimed.Add(c.Node);
            return c.Node;
        }
        return null;
    }

    private static List<HtmlNode> TakeAllAsides(List<Candidate> cands, HashSet<HtmlNode> claimed,
        HtmlNode? header, HtmlNode? nav, HtmlNode? footer)
    {
        var list = new List<HtmlNode>();
        foreach (var c in cands
                     .Where(c => c.Kind == "aside" && !claimed.Contains(c.Node))
                     .OrderByDescending(c => c.Strength)
                     .ThenBy(c => c.Order))
        {
            // Keep the outer aside; drop asides inside header/nav/footer chrome.
            // Asides inside <main> are the classic sidebar — always keep those.
            bool skip = false;
            for (var p = c.Node.ParentNode; p != null; p = p.ParentNode)
            {
                if (ReferenceEquals(p, header) || ReferenceEquals(p, nav) || ReferenceEquals(p, footer)
                    || list.Any(a => ReferenceEquals(a, p))) { skip = true; break; }
                if (p.Name.Equals("body", StringComparison.OrdinalIgnoreCase)) break;
            }
            if (skip) continue;
            claimed.Add(c.Node);
            list.Add(c.Node);
        }
        return list;
    }

    /// <summary>
    /// Zero-hook fallback: the body child with the most real (non-link) text wins.
    /// Claims of other regions are excluded so chrome never becomes "main".
    /// </summary>
    private static HtmlNode? FallbackMain(HtmlDocument doc, HashSet<HtmlNode> claimed)
    {
        var body = doc.DocumentNode.SelectSingleNode("//body") ?? doc.DocumentNode;
        HtmlNode? best = null;
        double bestScore = 200; // minimum viable article size
        foreach (var kid in body.ChildNodes.Where(n => n.NodeType == HtmlNodeType.Element))
        {
            if (claimed.Contains(kid)) continue;
            string tag = kid.Name.ToLowerInvariant();
            if (tag is "script" or "style" or "noscript" or "svg") continue;
            double score = ContentScore(kid);
            if (score > bestScore) { bestScore = score; best = kid; }
        }
        return best;
    }

    private static double ContentScore(HtmlNode node)
    {
        string all = HtmlEntity.DeEntitize(node.InnerText ?? "");
        double text = Regex.Replace(all, @"\s+", " ").Trim().Length;
        double linkText = 0;
        foreach (var a in node.SelectNodes(".//a") ?? Enumerable.Empty<HtmlNode>())
            linkText += Regex.Replace(HtmlEntity.DeEntitize(a.InnerText ?? ""),
                @"\s+", " ").Trim().Length;
        int headings = node.SelectNodes(".//h1|.//h2|.//h3")?.Count ?? 0;
        return text - 2 * linkText + headings * 50;
    }
}
