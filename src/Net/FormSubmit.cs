using System.Text;
using TexBowser.Layout;

namespace TexBowser.Net;

/// <summary>
/// Pure form-submission logic (fully unit-testable, no UI, no network):
/// gathering control states per HTML rules, url-encoding, GET URL building,
/// and POST body construction (urlencoded + multipart with real file upload).
/// </summary>
public sealed record FieldData(string Name, string Value);

public sealed record FileData(string Name, string FilePath);

/// <summary>Abstract control state; the renderer maps live views onto this.</summary>
public sealed record ControlState(FormControl Control, string Text, bool Checked, int Selected);

public static class FormSubmit
{
    private static string Esc(string? s) => Uri.EscapeDataString(s ?? "");

    public static string ToQuery(IEnumerable<FieldData> fields) =>
        string.Join("&", fields
            .Where(f => f.Name.Length > 0)
            .Select(f => Esc(f.Name) + "=" + Esc(f.Value)));

    /// <summary>
    /// Gather successful controls: named, enabled; checked boxes/radios;
    /// selected option; clicked submitter. Mirrors HTML form semantics.
    /// </summary>
    public static (List<FieldData> Fields, List<FileData> Files) Gather(
        IEnumerable<ControlState> states, string? submitterName, string? submitterValue)
    {
        var fields = new List<FieldData>();
        var files = new List<FileData>();
        foreach (var s in states)
        {
            var c = s.Control;
            if (c.Disabled) continue;
            switch (c.Kind)
            {
                case "text" or "password" or "search" or "url" or "email" or "tel"
                    or "number" or "textarea" or "hidden":
                    if (c.Name.Length > 0) fields.Add(new FieldData(c.Name, s.Text));
                    break;
                case "checkbox":
                    if (s.Checked && c.Name.Length > 0)
                        fields.Add(new FieldData(c.Name, c.Value.Length > 0 ? c.Value : "on"));
                    break;
                case "radio":
                    if (s.Checked && c.Name.Length > 0)
                        fields.Add(new FieldData(c.Name, c.Value));
                    break;
                case "select":
                    if (c.Name.Length > 0 && c.Options.Count > 0)
                    {
                        int idx = Math.Clamp(s.Selected, 0, c.Options.Count - 1);
                        fields.Add(new FieldData(c.Name, c.Options[idx].Value));
                    }
                    break;
                case "file":
                    if (c.Name.Length > 0 && s.Text.Length > 0)
                    {
                        string path = s.Text.Trim().Trim('"');
                        fields.Add(new FieldData(c.Name, Path.GetFileName(path)));
                        files.Add(new FileData(c.Name, path));
                    }
                    break;
                // submit/reset/button/image: only the clicked submitter counts (below).
            }
        }
        if (!string.IsNullOrEmpty(submitterName))
            fields.Add(new FieldData(submitterName, submitterValue ?? ""));
        return (fields, files);
    }

    /// <summary>GET: action with fragment stripped and query replaced.</summary>
    public static string GetUrl(string actionUrl, IEnumerable<FieldData> fields)
    {
        int hash = actionUrl.IndexOf('#');
        string baseUrl = hash >= 0 ? actionUrl[..hash] : actionUrl;
        int q = baseUrl.IndexOf('?');
        if (q >= 0) baseUrl = baseUrl[..q];
        string query = ToQuery(fields);
        return query.Length == 0 ? baseUrl : baseUrl + "?" + query;
    }

    public static HttpContent BuildPostContent(
        IEnumerable<FieldData> fields, IEnumerable<FileData> files, bool multipart)
    {
        var fieldList = fields.Where(f => f.Name.Length > 0).ToList();
        if (!multipart)
            return new StringContent(ToQuery(fieldList), Encoding.UTF8,
                "application/x-www-form-urlencoded");
        var mp = new MultipartFormDataContent();
        foreach (var f in fieldList)
            mp.Add(new StringContent(f.Value, Encoding.UTF8), f.Name);
        foreach (var fl in files.Where(f => f.Name.Length > 0))
        {
            if (File.Exists(fl.FilePath))
            {
                var sc = new StreamContent(File.OpenRead(fl.FilePath));
                sc.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                    GuessMime(fl.FilePath));
                mp.Add(sc, fl.Name, Path.GetFileName(fl.FilePath));
            }
            else
            {
                mp.Add(new StringContent(Path.GetFileName(fl.FilePath), Encoding.UTF8), fl.Name);
            }
        }
        return mp;
    }

    public static string GuessMime(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".txt" or ".log" or ".md" => "text/plain",
            ".html" or ".htm" => "text/html",
            ".json" => "application/json",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            _ => "application/octet-stream",
        };
}
