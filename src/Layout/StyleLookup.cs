using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace TexBowser.Layout;

/// <summary>
/// Effective-style resolution: merges document &lt;style&gt; rules with inline
/// style="" attributes so detectors see the real geometry (display, widths,
/// flex, grid tracks, floats) even when it lives in a stylesheet.
/// Only simple selectors (.cls, #id, tag) and layout properties are kept.
/// </summary>
public sealed class StyleLookup
{
    private static readonly Regex Rule =
        new(@"([^{}]+)\{([^{}]*)\}", RegexOptions.Compiled);

    private static readonly HashSet<string> KeptProps = new(StringComparer.OrdinalIgnoreCase)
    {
        "display", "width", "flex", "flex-grow", "flex-basis", "flex-direction",
        "grid-template-columns", "float", "flex-wrap",
    };

    private readonly Dictionary<string, Dictionary<string, string>> _byClass = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _byTag = new(StringComparer.OrdinalIgnoreCase);

    public static StyleLookup Parse(HtmlDocument doc)
    {
        var lookup = new StyleLookup();
        var styleNodes = doc.DocumentNode.SelectNodes("//style");
        if (styleNodes == null) return lookup;
        foreach (var node in styleNodes)
            lookup.AddSheet(node.InnerText ?? "");
        return lookup;
    }

    private void AddSheet(string css)
    {
        css = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
        foreach (Match m in Rule.Matches(css))
        {
            var decls = ParseDecls(m.Groups[2].Value);
            if (decls.Count == 0) continue;
            foreach (var sel in m.Groups[1].Value.Split(','))
            {
                string s = Regex.Replace(sel.Trim(), @"\s+", " ");
                if (s.Length == 0 || s.Contains(' ') || s.Contains(':') || s.Contains('[')) continue;
                if (s.StartsWith('.') && s.Length > 1)
                    Merge(_byClass, s[1..], decls);
                else if (s.StartsWith('#') && s.Length > 1)
                    Merge(_byId, s[1..], decls);
                else if (Regex.IsMatch(s, @"^[a-zA-Z][a-zA-Z0-9]*$"))
                    Merge(_byTag, s, decls);
                else if (Regex.IsMatch(s, @"^([a-zA-Z][a-zA-Z0-9]*)\.([a-zA-Z0-9_-]+)$"))
                {
                    var mm = Regex.Match(s, @"^([a-zA-Z][a-zA-Z0-9]*)\.([a-zA-Z0-9_-]+)$");
                    Merge(_byClass, mm.Groups[2].Value, decls);
                }
            }
        }
    }

    private static Dictionary<string, string> ParseDecls(string body)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in body.Split(';'))
        {
            int colon = part.IndexOf(':');
            if (colon <= 0) continue;
            string prop = part[..colon].Trim().ToLowerInvariant();
            string val = part[(colon + 1)..].Trim().ToLowerInvariant();
            if (prop.Length > 0 && val.Length > 0 && KeptProps.Contains(prop))
                dict[prop] = val;
        }
        return dict;
    }

    private static void Merge(Dictionary<string, Dictionary<string, string>> map,
        string key, Dictionary<string, string> decls)
    {
        if (!map.TryGetValue(key, out var cur))
        {
            cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            map[key] = cur;
        }
        foreach (var kv in decls) cur[kv.Key] = kv.Value;
    }

    /// <summary>Effective declarations: tag &lt; classes (in order) &lt; id &lt; inline.</summary>
    public Dictionary<string, string> Effective(HtmlNode el)
    {
        var eff = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_byTag.TryGetValue(el.Name, out var tag)) foreach (var kv in tag) eff[kv.Key] = kv.Value;
        foreach (var cls in ClassesOf(el))
            if (_byClass.TryGetValue(cls, out var cr)) foreach (var kv in cr) eff[kv.Key] = kv.Value;
        string id = el.GetAttributeValue("id", "");
        if (id.Length > 0 && _byId.TryGetValue(id, out var ir)) foreach (var kv in ir) eff[kv.Key] = kv.Value;
        foreach (var kv in ParseDecls(el.GetAttributeValue("style", ""))) eff[kv.Key] = kv.Value;
        return eff;
    }

    public static List<string> ClassesOf(HtmlNode el) =>
        el.GetAttributeValue("class", "")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .ToList();

    public static string? Get(Dictionary<string, string> eff, string prop) =>
        eff.TryGetValue(prop, out var v) ? v : null;
}
