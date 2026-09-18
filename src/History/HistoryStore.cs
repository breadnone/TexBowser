using System.Text.Json;

namespace TexBowser.History;

/// <summary>
/// Plain browsing history (no learning): one row per URL with visit counts,
/// persisted as JSON, capped to keep the file small. Thread-safe.
/// </summary>
public sealed class HistoryStore
{
    private sealed record Entry(string Url, string Title, int Visits, string FirstSeen, string LastSeen);

    public sealed record HistoryRow(string Url, string Title, int Visits, string FirstSeen, string LastSeen);

    private const int Cap = 500;
    private readonly string _path;
    private readonly object _gate = new();
    private List<Entry> _entries = new();

    public HistoryStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "history.json");
        Load();
    }

    public void Record(string url, string title)
    {
        lock (_gate)
        {
            string now = DateTime.UtcNow.ToString("o");
            var existing = _entries.FirstOrDefault(e => e.Url == url);
            if (existing == null)
                _entries.Add(new Entry(url, title ?? "", 1, now, now));
            else
            {
                _entries.Remove(existing);
                _entries.Add(existing with
                {
                    Title = string.IsNullOrEmpty(title) ? existing.Title : title,
                    Visits = existing.Visits + 1,
                    LastSeen = now,
                });
            }
            // Most-recently-used order, capped.
            _entries = _entries
                .OrderByDescending(e => e.LastSeen, StringComparer.Ordinal)
                .Take(Cap)
                .ToList();
            Save();
        }
    }

    public List<HistoryRow> List()
    {
        lock (_gate)
            return _entries
                .OrderByDescending(e => e.LastSeen, StringComparer.Ordinal)
                .Select(e => new HistoryRow(e.Url, e.Title, e.Visits, e.FirstSeen, e.LastSeen))
                .ToList();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var loaded = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_path));
            if (loaded != null) _entries = loaded.Take(Cap).ToList();
        }
        catch { /* corrupt file: start fresh */ }
    }

    private void Save()
    {
        try
        {
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_entries));
            File.Move(tmp, _path, overwrite: true);
        }
        catch { /* history is best-effort; never break browsing */ }
    }
}
