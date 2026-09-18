using TexBowser.Layout;

namespace TexBowser.Ui;

/// <summary>What an overlay page (history / error) shows while the tab's real page stays cached.</summary>
public enum TabOverlayKind
{
    None,
    History,
    Error,
}

/// <summary>Per-tab browsing state: location, source, session stacks, scroll.</summary>
public sealed class TabState
{
    public string Url { get; set; } = "";
    public string Html { get; set; } = "";
    public string Title { get; set; } = "";
    public Stack<string> Back { get; } = new();
    public Stack<string> Forward { get; } = new();
    public int ScrollY { get; set; }

    /// <summary>Numbered links of the currently displayed page (for bare-number nav).</summary>
    public IReadOnlyList<LinkInfo> Links { get; set; } = Array.Empty<LinkInfo>();

    /// <summary>Width-independent perception cache: reflow/switch never re-perceives.</summary>
    public PerceivedPage? Page { get; set; }

    /// <summary>Viewport width the tab was last rendered at (lazy reflow on resize).</summary>
    public int LastWidth { get; set; }

    /// <summary>An in-flight load, if any (Stop cancels it).</summary>
    public bool IsLoading { get; set; }

    public CancellationTokenSource? LoadCts { get; set; }

    /// <summary>History/error overlay shown over the cached page, if any.</summary>
    public TabOverlayKind Overlay { get; set; } = TabOverlayKind.None;

    /// <summary>Bumped every time an overlay opens; lets late loads tell a
    /// stale overlay (opened before the load) from a fresh user action.</summary>
    public int OverlaySeq { get; set; }

    public string OverlayCaption { get; set; } = "";
    public List<string> OverlayLines { get; set; } = new();
    public IReadOnlyList<LinkInfo> OverlayLinks { get; set; } = Array.Empty<LinkInfo>();
    public string OverlayErrorInput { get; set; } = "";
    public string OverlayErrorMessage { get; set; } = "";

    public bool CanGoBack => Back.Count > 0;
    public bool CanGoForward => Forward.Count > 0;

    /// <summary>Short tab-strip label: loading spinner, else title-or-URL truncated.</summary>
    public string GetLabel()
    {
        if (IsLoading) return GetLoadingLabel();
        string name = Title.Length > 0 ? Title : Url;
        if (string.IsNullOrEmpty(name)) name = "new tab";
        return name.Length > 22 ? name[..22] + "…" : name;
    }

    /// <summary>
    /// Animated loading label driven by wall-clock (headless-testable, no
    /// timer state): a spinning braille dot plus elapsed seconds.
    /// </summary>
    public string GetLoadingLabel()
    {
        const string frames = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";
        int frame = (int)(Environment.TickCount64 / 120 % frames.Length);
        return $"{frames[frame]} loading…";
    }

    public void CancelLoad()
    {
        try { LoadCts?.Cancel(); }
        catch (ObjectDisposedException) { /* already gone */ }
        LoadCts = null;
        IsLoading = false;
    }

    public void ClearOverlay()
    {
        Overlay = TabOverlayKind.None;
        OverlayCaption = "";
        OverlayLines = new List<string>();
        OverlayLinks = Array.Empty<LinkInfo>();
        OverlayErrorInput = "";
        OverlayErrorMessage = "";
    }
}

/// <summary>
/// Pure tab-strip model (headless-testable): open/switch/close with per-tab
/// state, a 10-tab cap, and a last-tab-close that reseeds a home tab.
/// Views are rebuilt from cached HTML on every switch — no view state here.
/// </summary>
public sealed class TabsModel
{
    public const int MaxTabs = 10;
    private readonly string _home;
    public List<TabState> Tabs { get; } = new();
    public int Active { get; private set; }

    public TabsModel(string home)
    {
        _home = home;
        Tabs.Add(new TabState { Url = home });
    }

    public TabState Current => Tabs[Active];
    public bool CanNew => Tabs.Count < MaxTabs;

    public void NewTab()
    {
        if (!CanNew) return;
        Tabs.Add(new TabState { Url = _home });
        Active = Tabs.Count - 1;
    }

    public void SwitchTo(int i)
    {
        if (i >= 0 && i < Tabs.Count) Active = i;
    }

    public void Next() => Active = (Active + 1) % Tabs.Count;
    public void Prev() => Active = (Active - 1 + Tabs.Count) % Tabs.Count;

    public void CloseTab(int i)
    {
        if (i < 0 || i >= Tabs.Count) return;
        Tabs.RemoveAt(i);
        if (Tabs.Count == 0)
        {
            Tabs.Add(new TabState { Url = _home });
            Active = 0;
            return;
        }
        if (i < Active) Active--;
        else if (Active >= Tabs.Count) Active = Tabs.Count - 1;
    }

    /// <summary>
    /// Move the active tab by <paramref name="delta"/> (±1 typical).
    /// The moved tab stays active at its new index. Out-of-range moves
    /// are a no-op returning false (caller notifies).
    /// </summary>
    public bool MoveActive(int delta)
    {
        int from = Active;
        int to = from + delta;
        if (delta == 0 || from < 0 || from >= Tabs.Count || to < 0 || to >= Tabs.Count)
            return false;
        var tab = Tabs[from];
        Tabs.RemoveAt(from);
        Tabs.Insert(to, tab);
        Active = to;
        return true;
    }

    /// <summary>
    /// Close every tab strictly to the right of <paramref name="anchor"/>.
    /// The anchor tab itself is never closed, so no home reseed is needed.
    /// If the active tab was among the removed ones it falls back to the
    /// anchor. Returns the number of tabs closed (0 = nothing to close).
    /// </summary>
    public int CloseTabsToRight(int anchor)
    {
        if (anchor < 0 || anchor >= Tabs.Count) return 0;
        int removeCount = Tabs.Count - anchor - 1;
        if (removeCount <= 0) return 0;
        Tabs.RemoveRange(anchor + 1, removeCount);
        if (Active > anchor) Active = anchor;
        return removeCount;
    }
}
