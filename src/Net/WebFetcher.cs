namespace TexBowser.Net;

/// <summary>Minimal HTTP fetcher with a browser UA, redirects and friendly errors.</summary>
public sealed class WebFetcher
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
    })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    static WebFetcher()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (compatible; TexBowser/1.0; text web browser)");
        Http.DefaultRequestHeaders.Accept.ParseAdd("text/html,*/*");
    }

    public static string Normalize(string input)
    {
        input = (input ?? "").Trim();
        if (input.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return input;
        if (!input.Contains("://", StringComparison.Ordinal)) input = "https://" + input;
        return input;
    }

    public async Task<(string Html, string FinalUrl)> FetchAsync(string url, CancellationToken ct = default)
    {
        url = Normalize(url);
        try
        {
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            string html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            string final = resp.RequestMessage?.RequestUri?.ToString() ?? url;
            return (html, final);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Could not fetch {url}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex)
        {
            // User Stop (canceled token) must surface as cancellation so the UI
            // reports "stopped"; genuine timeouts keep the timeout message.
            if (ct.IsCancellationRequested) throw new OperationCanceledException("Stopped.", ex, ct);
            throw new InvalidOperationException($"Timed out fetching {url}.", ex);
        }
    }

    public async Task<(string Body, string FinalUrl, string? ContentType)> PostFormAsync(
        string url, HttpContent content, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            string? media = resp.Content.Headers.ContentType?.MediaType;
            string final = resp.RequestMessage?.RequestUri?.ToString() ?? url;
            return (body, final, media);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Form submit to {url} failed: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException("Stopped.", ex, ct);
            throw new InvalidOperationException($"Timed out submitting to {url}.", ex);
        }
    }
}
