using TexBowser.Demo;
using TexBowser.History;
using TexBowser.Layout;
using TexBowser.Net;
using TexBowser.Render;

namespace TexBowser.Engine;

/// <summary>
/// Detection-only pipeline: fetch → perceive → reconstruct.
/// No learning, no database of layouts — every page is analyzed fresh.
/// UI-free so headless --dump and --selftest can drive it directly.
/// </summary>
public sealed class BrowserEngine : IDisposable
{
    public HistoryStore History { get; }
    private readonly WebFetcher _fetcher = new();
    private bool _disposed;

    public string DataDir { get; }

    public BrowserEngine(string dataDir)
    {
        DataDir = dataDir;
        History = new HistoryStore(dataDir);
    }

    public static string DefaultDataDir()
    {
        string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(baseDir)) baseDir = Directory.GetCurrentDirectory();
        return Path.Combine(baseDir, "TexBowser");
    }

    public async Task<RenderedPage> LoadAsync(string input, int viewportWidth, CancellationToken ct = default)
    {
        var (html, finalUrl) = await FetchHtmlAsync(input, ct).ConfigureAwait(false);
        return RenderHtml(html, finalUrl, viewportWidth);
    }

    public async Task<(string Html, string FinalUrl)> FetchHtmlAsync(string input, CancellationToken ct = default)
    {
        string normalized = WebFetcher.Normalize(input);
        string? demo = DemoPages.Get(normalized);
        if (demo != null) return (demo, normalized);
        return await _fetcher.FetchAsync(normalized, ct).ConfigureAwait(false);
    }

    public WebFetcher Poster => _fetcher;

    public RenderedPage RenderHtml(string html, string url, int viewportWidth, bool recordHistory = true)
    {
        PerceivedPage page = LayoutPerceiver.Perceive(html, url);
        RenderedPage rendered = PageRenderer.Render(page, url, viewportWidth);
        if (recordHistory) History.Record(url, page.Title);
        return rendered;
    }

    /// <summary>
    /// History page (most recent first) plus followable links, so typing a
    /// number in the url box opens that entry — like picking from history.
    /// </summary>
    public (List<string> Lines, List<LinkInfo> Links) HistoryLines(int width)
    {
        var rows = History.List();
        var lines = new List<string> { new string('═', width), "◈ history — most recent first", "" };
        var links = new List<LinkInfo>();
        if (rows.Count == 0)
        {
            lines.Add("No history yet. Open some pages and they will be logged here,");
            lines.Add("one row per URL with visit counts, across sessions.");
        }
        else
        {
            int n = 0;
            foreach (var r in rows.Take(100))
            {
                n++;
                string name = r.Title.Length > 0 ? r.Title : r.Url;
                if (name.Length > 52) name = name[..52] + "…";
                string meta = $"{r.Visits}× · {ShortTime(r.LastSeen)}";
                string entry = $"[{n}] {name}";
                if (entry.Length + meta.Length + 3 <= width)
                    entry = entry.PadRight(width - meta.Length - 3) + " · " + meta;
                lines.Add(entry);
                lines.Add("    → " + (r.Url.Length > width - 8 ? r.Url[..(width - 8)] + "…" : r.Url));
                links.Add(new LinkInfo(n, name, r.Url));
            }
            lines.Add("");
            lines.Add("Type a number in the url box to open it · :clear-history wipes the log");
        }
        lines.Add(new string('═', width));
        return (lines, links);
    }

    private static string ShortTime(string iso) =>
        DateTime.TryParse(iso, out var dt)
            ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : iso;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
