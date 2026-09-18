using System.Drawing;
using System.Net;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TexBowser.Demo;
using TexBowser.Engine;
using TexBowser.Layout;
using TexBowser.Net;
using TexBowser.Render;

namespace TexBowser.Ui;

/// <summary>
/// Terminal.Gui v2 front-end: a single nav row (back, forward, reload/stop,
/// url box, go, history), a browser-style tab strip row (real <see cref="Tabs"/>
/// + new-tab button next to the tabs + one close button top-right), one
/// persistent page slot per browser tab, and async stoppable loads. Each slot
/// is one scrolling canvas plus live form controls, so big pages stay smooth
/// and focus can never get lost among hundreds of paragraph views.
/// url-box mini-language: a bare number follows that link (or opens that history
/// entry); :back :fwd :reload :history :clear-history :tabnew :tabclose;
/// about:home (landing) about:demo about:demo-cards about:demo-table about:demo-search.
/// Right-click the tab strip for close / close-right menu; Ctrl+Shift+Left/Right
/// reorders the active tab.
/// </summary>
public sealed class BrowserApp
{
    private readonly BrowserEngine _engine;
    private TabsModel _tabs = new("about:home");
    private string _homeUrl = "about:home";
    private TabState Tab => _tabs.Current;

    private string _pageCaption = "TexBowser";

    private Window _win = null!;
    private TextField _urlField = null!;
    private Button _backBtn = null!;
    private Button _fwdBtn = null!;
    private Button _reloadBtn = null!;
    private Tabs _tabsView = null!;
    private IApplication? _app;
    private readonly List<View> _slots = new();
    private PageActions _pageActions = null!;

    private int _activeWidth;
    private View? _restoreSlot;
    private int _restoreY;

    private View? _findBar;
    private TextField? _findField;
    private Label? _findStatus;
    private PageTextView? _findCanvas;

    /// <summary>
    /// Columns reserved at the right of the tab strip row for the [+] and
    /// [x] buttons (5 + 1 gap + 5 + 1 gap). Tabs uses Width=Fill(reserve);
    /// + sits at AnchorEnd()-6, x at AnchorEnd(). Single source of truth
    /// for layout and the headless tab-strip test.
    /// </summary>
    internal const int TabStripReserve = 12;

    public BrowserApp(BrowserEngine engine) => _engine = engine;

    /// <summary>Renderer factory: always carries the running app instance so
    /// context menus can register popovers (never the static model).</summary>
    private ViewRenderer NewRenderer(int w, string url, IReadOnlyList<LinkInfo> links, IBrowserActions actions) =>
        new(w, url, links, actions) { App = _app };

    private sealed class PageActions : IBrowserActions
    {
        public BrowserApp? App;
        public void Navigate(string url) => App?.Navigate(url);
        public void SubmitPost(string actionUrl, List<FieldData> fields, List<FileData> files, bool multipart) =>
            App?.SubmitPost(actionUrl, fields, files, multipart);
        public void OpenResult(string body, string finalUrl, string? contentType) =>
            App?.OpenResult(body, finalUrl, contentType);
        public void Notify(string message) => App?.Notify(message);
        public void PreviewLink(string? url) => App?.PreviewLink(url);
        public void CopyToClipboard(string text) => App?.CopyToClipboard(text);
        public void TakeScreenshot() => App?.TakeScreenshot();
    }

    public void Run(string? startUrl)
    {
        using IApplication app = Application.Create();
        app.Init();
        app.Mouse.IsMouseDisabled = false;
        _app = app;

        _homeUrl = string.IsNullOrWhiteSpace(startUrl) ? "about:home" : startUrl;
        _tabs = new TabsModel(_homeUrl);
        _win = new Window { Title = "TexBowser" };
        _pageActions = new PageActions { App = this };

        // Row 0: address chrome (back, forward, reload/stop, url, go, hist).
        // Plain ASCII labels only: geometric glyphs (tofu in many console
        // fonts) once made these buttons look like they did not exist at all.
        var navRow = BuildNavRow(out var goBtn, out var histBtn);
        _win.Add(navRow);

        // Row 1: browser-style tab strip. The Tabs control keeps full tab
        // logic (real slots, ValueChanged switching); the + and x are plain
        // sibling buttons docked right in the same row (never children of
        // Tabs, which would turn them into extra tabs). Tabs leaves 12 cols
        // free: [+] [x] with 1-col gaps, exactly like the address row math.
        _tabsView = new Tabs { X = 0, Y = 1, Width = Dim.Fill(TabStripReserve), Height = Dim.Fill() };
        _tabsView.ValueChanged += OnTabControlChanged;
        _win.Add(_tabsView);
        var tabBtns = BuildTabRowButtons(out var newTabBtn, out var closeTabBtn);
        _win.Add(tabBtns);
        _tabsView.MouseEvent += (_, m) =>
        {
            if (m is null) return;
            if (!m.Flags.HasFlag(Terminal.Gui.Input.MouseFlags.RightButtonClicked)) return;
            ShowTabContextMenu(m);
            m.Handled = true;
        };

        // Keyboard shortcuts: Ctrl+T/W/Tab/L/R + Ctrl+Shift+Left/Right move.
        // Control combos never insert text, so handling them here cannot
        // disturb typing; mark handled.
        BindHotkeys();

        goBtn.Accepting += (_, _) => Navigate(_urlField.Text?.ToString() ?? "");
        _urlField.Accepting += (_, _) => Navigate(_urlField.Text?.ToString() ?? "");
        _backBtn.Accepting += (_, _) => GoBack();
        _fwdBtn.Accepting += (_, _) => GoForward();
        _reloadBtn.Accepting += (_, _) =>
        {
            if (Tab.IsLoading) StopLoad();
            else Reload();
        };
        histBtn.Accepting += (_, _) => ShowHistory();
        newTabBtn.Accepting += (_, _) => NewTab();
        closeTabBtn.Accepting += (_, _) => CloseTab();

        // First paint is synchronous so chrome, tab strip and page content are
        // visible immediately, even before the loop pumps: demo homes render
        // fully, remote homes paint a loading placeholder. A timer covers the
        // remote case by kicking off its fetch once the loop runs.
        _activeWidth = ContentWidth();
        AddSlotFor(0);
        try { _tabsView.Value = _slots[0]; } catch { /* header syncs on paint */ }
        PaintFirstTab();
        // Instance timer (never the static Application model — mixing the two
        // crashes the process on v2). Safety net only: starts the home load if
        // nothing is already in flight or painted.
        app.AddTimeout(TimeSpan.FromMilliseconds(50), () =>
        {
            try
            {
                if (!Tab.IsLoading && Tab.Html.Length == 0 && Tab.Page == null
                    && Tab.Overlay == TabOverlayKind.None)
                    ActivateTab();
            }
            catch { /* best-effort */ }
            return false;
        });

        // Watchdog: pending scroll restores + resize reflow (active tab only).
        app.Iteration += (_, _) =>
        {
            try
            {
                if (_restoreSlot != null)
                {
                    var canvas = FindCanvas(_restoreSlot);
                    if (canvas != null && canvas.Viewport.Height > 0)
                    {
                        int maxY = Math.Max(0, canvas.DocLines.Count - canvas.Viewport.Height);
                        canvas.Viewport = canvas.Viewport with { Y = Math.Clamp(_restoreY, 0, maxY) };
                        _restoreSlot = null;
                    }
                    else if (canvas == null)
                    {
                        _restoreSlot = null;
                    }
                }
                int w = ContentWidth();
                if (w != _activeWidth && w >= 40)
                {
                    _activeWidth = w;
                    ReflowActive();
                }
                // Animate the loading spinner: refresh tab titles (labels)
                // while any tab is still fetching.
                if (_tabs.Tabs.Any(t => t.IsLoading))
                {
                    for (int i = 0; i < _slots.Count && i < _tabs.Tabs.Count; i++)
                    {
                        try { _slots[i].Title = _tabs.Tabs[i].GetLabel(); }
                        catch { /* headless */ }
                    }
                }
            }
            catch { /* housekeeping is best-effort */ }
        };

        app.Run(_win);

        foreach (var tab in _tabs.Tabs) tab.CancelLoad();
    }

    // ---------------- navigation (per-tab, async, stoppable) ----------------

    private void Navigate(string input, bool pushHistory = true)
    {
        input = (input ?? "").Trim();
        if (input.Length == 0) return;

        // Bare number → follow that link (or open that history entry).
        if (int.TryParse(input, out int n) && n >= 1 && n <= Tab.Links.Count)
        {
            Navigate(Tab.Links[n - 1].Url, pushHistory: true);
            _urlField.Text = Tab.Url;
            return;
        }

        switch (input.ToLowerInvariant())
        {
            case ":back":
            case ":backward":
                GoBack();
                return;
            case ":fwd":
            case ":forward":
                GoForward();
                return;
            case ":reload":
            case ":refresh":
                Reload();
                return;
            case ":history":
                ShowHistory();
                return;
            case ":clear-history":
                _engine.History.Clear();
                ShowOverlay(TabOverlayKind.History,
                    new List<string> { "Browsing log wiped. New visits will be logged here." },
                    Array.Empty<LinkInfo>(), "History");
                return;
            case ":tabnew":
                NewTab();
                return;
            case ":tabclose":
                CloseTab();
                return;
        }

        var tab = Tab;
        BeginLoad(tab, ct => _engine.FetchHtmlAsync(input, ct), pushHistory, label: input);
    }

    /// <summary>
    /// Start an async load on a tab: cancels any previous load there, fetches
    /// + perceives + flows off the UI thread, then applies on it. Late or
    /// cancelled results are dropped (supersede check).
    /// </summary>
    private void BeginLoad(TabState tab, Func<CancellationToken, Task<(string Html, string FinalUrl)>> fetcher,
        bool pushHistory, string label = "")
    {
        // Landing page short-circuit: about:home / about:blank never hit the
        // network, never record history, and render centered (not perceived).
        // Single choke point covers Navigate, Back/Forward, Reload and the
        // ActivateTab blank-tab path (all pass the target url as label).
        if (IsHomeUrl(label))
        {
            tab.CancelLoad();
            if (pushHistory && tab.Url.Length > 0 && !IsHomeUrl(tab.Url))
            {
                tab.Back.Push(tab.Url);
                tab.Forward.Clear();
            }
            RenderHomeTab(tab, label);
            return;
        }
        tab.CancelLoad();
        var cts = new CancellationTokenSource();
        tab.LoadCts = cts;
        tab.IsLoading = true;
        int width = ContentWidth();
        int overlaySeq = tab.OverlaySeq;
        SetInfo($"loading …");
        RefreshChrome();

        _ = Task.Run(async () =>
        {
            try
            {
                var (html, finalUrl) = await fetcher(cts.Token).ConfigureAwait(false);
                cts.Token.ThrowIfCancellationRequested();
                var perceived = LayoutPerceiver.Perceive(html, finalUrl);
                cts.Token.ThrowIfCancellationRequested();
                var worker = NewRenderer(width, finalUrl, perceived.Links, _pageActions);
                var built = worker.BuildFlow(perceived, width);
                cts.Token.ThrowIfCancellationRequested();
                try
                {
                    _app?.Invoke(() => ApplyLoadedPage(tab, cts, overlaySeq, html, finalUrl, perceived, built, width, pushHistory));
                }
                catch { /* shut down mid-load */ }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                try
                {
                    _app?.Invoke(() =>
                    {
                        if (!_tabs.Tabs.Contains(tab) || tab.LoadCts != cts) return;
                        tab.IsLoading = false;
                        tab.LoadCts = null;
                        if (tab == Tab) { RefreshChrome(); SetInfo("stopped"); }
                    });
                }
                catch { /* shutting down */ }
            }
            catch (Exception ex)
            {
                try
                {
                    _app?.Invoke(() =>
                    {
                        if (!_tabs.Tabs.Contains(tab) || tab.LoadCts != cts) return;
                        tab.IsLoading = false;
                        tab.LoadCts = null;
                        tab.OverlayErrorInput = label.Length > 0 ? label : tab.Url;
                        tab.OverlayErrorMessage = ex.Message;
                        ShowErrorOverlay(tab, ex);
                        if (tab == Tab) RefreshChrome();
                    });
                }
                catch { /* shutting down */ }
            }
        });
    }

    private void ApplyLoadedPage(TabState tab, CancellationTokenSource cts, int overlaySeq, string html, string finalUrl,
        PerceivedPage perceived, ViewRenderer.Flow built, int builtWidth, bool pushHistory)
    {
        // Safety net: a home URL should never arrive via fetch (BeginLoad
        // short-circuits), but a late in-flight result must still render
        // centered rather than as a perceived flow.
        if (IsHomeUrl(finalUrl))
        {
            if (!_tabs.Tabs.Contains(tab) || tab.LoadCts != cts || cts.IsCancellationRequested) return;
            tab.ClearOverlay();
            if (pushHistory && tab.Url.Length > 0 && !IsHomeUrl(tab.Url))
            {
                tab.Back.Push(tab.Url);
                tab.Forward.Clear();
            }
            RenderHomeTab(tab, finalUrl);
            return;
        }
        if (!_tabs.Tabs.Contains(tab) || tab.LoadCts != cts || cts.IsCancellationRequested) return;
        // A fresh overlay opened mid-load wins over this stale result: keep the
        // page cached but leave the slot alone.
        bool keepOverlay = tab.OverlaySeq != overlaySeq;
        if (!keepOverlay)
        {
            tab.ClearOverlay();
            // Reflow at current width if the terminal was resized mid-load.
            int wNow = ContentWidth();
            ViewRenderer.Flow flow = built;
            int wUse = builtWidth;
            if (wNow != builtWidth && tab == Tab)
            {
                flow = NewRenderer(wNow, finalUrl, perceived.Links, _pageActions).BuildFlow(perceived, wNow);
                wUse = wNow;
            }
            ApplyPerceived(tab, html, finalUrl, perceived, flow, wUse, pushHistory);
        }
        else
        {
            // Cache the page behind the overlay; the slot keeps showing it.
            if (pushHistory && tab.Url.Length > 0 && finalUrl != tab.Url)
            {
                tab.Back.Push(tab.Url);
                tab.Forward.Clear();
            }
            tab.Url = finalUrl;
            tab.Html = html;
            tab.Title = perceived.Title;
            tab.Links = perceived.Links;
            tab.Page = perceived;
            tab.ScrollY = 0;
            tab.IsLoading = false;
            tab.LoadCts = null;
            try { _engine.History.Record(finalUrl, perceived.Title); } catch { /* best-effort */ }
            RefreshChrome();
        }
    }

    /// <summary>
    /// UI-thread page application shared by async completions and the
    /// synchronous first paint: updates model, history, slot views, chrome.
    /// </summary>
    private void ApplyPerceived(TabState tab, string html, string finalUrl,
        PerceivedPage perceived, ViewRenderer.Flow flow, int width, bool pushHistory)
    {
        if (pushHistory && tab.Url.Length > 0 && finalUrl != tab.Url)
        {
            tab.Back.Push(tab.Url);
            tab.Forward.Clear();
        }
        tab.Url = finalUrl;
        tab.Html = html;
        tab.Title = perceived.Title;
        tab.Links = perceived.Links;
        tab.Page = perceived;
        tab.LastWidth = width;
        tab.ScrollY = 0;
        tab.IsLoading = false;
        tab.LoadCts = null;
        try { _engine.History.Record(finalUrl, perceived.Title); } catch { /* best-effort */ }

        var slot = SlotFor(tab);
        // Lazy slots: only the ACTIVE tab keeps live views (canvas text buffer
        // + form controls). Background completions update the model only; the
        // views rebuild from the cached Page on activation (ActivateTab already
        // handles FindCanvas==null via RebuildSlot). Keeping N full page views
        // alive is what made many tabs unusable.
        if (slot != null && tab == Tab)
        {
            var renderer = NewRenderer(width, finalUrl, perceived.Links, _pageActions);
            renderer.BuildSlotViews(slot, flow);
            _restoreSlot = slot;
            _restoreY = 0;
        }
        if (tab == Tab)
        {
            // Don't yank focus out of the address box while the user is
            // already typing the *next* address (async completion vs. typing).
            bool typingNext = false;
            try { typingNext = _urlField.HasFocus && _urlField.Text?.ToString() != tab.Url; }
            catch { }
            _urlField.Text = tab.Url;
            SetCaption(Caption(tab.Title.Length > 0 ? tab.Title : tab.Url));
            RefreshChrome();
            if (!typingNext) FocusCanvas();
        }
        else
        {
            RefreshChrome();
        }
    }

    private void StopLoad()
    {
        Tab.CancelLoad();
        RefreshChrome();
        SetInfo("stopped");
    }

    private void GoBack()
    {
        var tab = Tab;
        if (tab.Back.Count == 0) { SetInfo("no back history"); return; }
        tab.Forward.Push(tab.Url);
        string url = tab.Back.Pop();
        BeginLoad(tab, ct => _engine.FetchHtmlAsync(url, ct), pushHistory: false, label: url);
    }

    private void GoForward()
    {
        var tab = Tab;
        if (tab.Forward.Count == 0) { SetInfo("no forward history"); return; }
        tab.Back.Push(tab.Url);
        string url = tab.Forward.Pop();
        BeginLoad(tab, ct => _engine.FetchHtmlAsync(url, ct), pushHistory: false, label: url);
    }

    private void CycleTab(int dir)
    {
        if (_tabs.Tabs.Count < 2) return;
        SaveScroll(Tab);
        if (dir < 0) _tabs.Prev();
        else _tabs.Next();
        _tabsView.Value = _slots[_tabs.Active];
        // ValueChanged syncs the rest.
    }

    private void Reload()
    {
        var tab = Tab;
        if (tab.Url.Length == 0 && tab.Html.Length == 0) { SetInfo("nothing to reload"); return; }
        string url = tab.Url.Length > 0 ? tab.Url : _homeUrl;
        BeginLoad(tab, ct => _engine.FetchHtmlAsync(url, ct), pushHistory: false, label: url);
    }

    // ---------------- tabs (real Tabs control) ----------------

    private View? SlotFor(TabState tab)
    {
        int i = _tabs.Tabs.IndexOf(tab);
        return (i >= 0 && i < _slots.Count) ? _slots[i] : null;
    }

    private void AddSlotFor(int modelIndex)
    {
        var slot = new View { Title = _tabs.Tabs[modelIndex].GetLabel() };
        _slots.Insert(modelIndex, slot);
        _tabsView.Add(slot);
    }

    private void NewTab()
    {
        if (!_tabs.CanNew) { Notify($"tab limit reached ({TabsModel.MaxTabs})"); return; }
        SaveScroll(Tab);
        _tabs.NewTab();
        AddSlotFor(_tabs.Active);
        _tabsView.Value = _slots[_tabs.Active];
        // ValueChanged handler activates (loads home into the blank tab).
        // Browser behavior: a fresh tab is for typing — land in the address
        // box with its text selected so the first keystroke replaces it.
        // Runs after the synchronous ValueChanged activation above, so this
        // focus wins over RenderHomeTab's FocusCanvas.
        FocusUrlForTyping(_urlField);
    }

    /// <summary>
    /// Focus the address box and select all its text (new-tab entry).
    /// Static + headless-testable; instance state untouched. Guards mirror
    /// FocusCanvas: never throws headless or mid-shutdown.
    /// </summary>
    internal static void FocusUrlForTyping(TextField urlField)
    {
        try { urlField.SetFocus(); } catch { /* best-effort */ }
        try { urlField.SelectAll(); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Synchronous first paint (same UI thread that will own the loop):
    /// offline demo homes render completely, remote homes get a loading
    /// placeholder until the timer-started fetch completes.
    /// </summary>
    private void PaintFirstTab()
    {
        var tab = Tab;
        var slot = SlotFor(tab);
        if (slot == null) return;
        // Landing page paints centered without any fetch or perceive.
        if (IsHomeUrl(tab.Url))
        {
            RenderHomeTab(tab, tab.Url);
            return;
        }
        string? demo = DemoPages.Get(WebFetcher.Normalize(tab.Url));
        if (demo == null)
        {
            int w = ContentWidth();
            var renderer = NewRenderer(w, tab.Url, tab.Links, _pageActions);
            renderer.RenderLines(slot, new List<string> { $"loading {tab.Url}…" });
            tab.LastWidth = w;
            RefreshChrome();
            FocusCanvas();
            return;
        }
        try
        {
            int w = ContentWidth();
            var perceived = LayoutPerceiver.Perceive(demo, tab.Url);
            var flow = NewRenderer(w, tab.Url, perceived.Links, _pageActions).BuildFlow(perceived, w);
            ApplyPerceived(tab, demo, tab.Url, perceived, flow, w, pushHistory: false);
        }
        catch (Exception ex)
        {
            tab.OverlayErrorInput = tab.Url;
            tab.OverlayErrorMessage = ex.Message;
            ShowErrorOverlay(tab, ex);
            RefreshChrome();
        }
    }

    private void ActivateTab()
    {
        try
        {
            ActivateTabCore();
        }
        finally
        {
            // Lazy slots: every activation ends with exactly one live tab.
            // Single choke point covers click/keyboard/menu/close/move paths
            // (all of them funnel through here via ValueChanged).
            UnloadBackgroundSlots();
        }
    }

    private void ActivateTabCore()
    {
        var t = Tab;
        _urlField.Text = t.Url;
        if (t.Overlay != TabOverlayKind.None)
        {
            RenderOverlay(t);
            RefreshChrome();
            return;
        }
        if (t.Html.Length == 0 || t.Page == null)
        {
            if (t.IsLoading) { RefreshChrome(); return; }
            if (t.Url.Length > 0) BeginLoad(t, ct => _engine.FetchHtmlAsync(t.Url, ct), pushHistory: false, label: t.Url);
            else RefreshChrome();
            return;
        }
        int w = ContentWidth();
        if (t.LastWidth != w)
        {
            RebuildSlot(t, w);
        }
        else
        {
            var slot = SlotFor(t);
            if (slot != null && FindCanvas(slot) == null)
                RebuildSlot(t, w);
        }
        RefreshChrome();
        FocusCanvas();
    }

    /// <summary>
    /// Free background tab views: for every non-active tab, persist its scroll
    /// offset into the model, then remove + dispose the slot's children (page
    /// canvas text buffer + live form controls), keeping the lightweight slot
    /// shell (Title) so the tab header survives. The model (Url/Html/Page/
    /// Links/Title/scroll) is untouched, so reactivation rebuilds purely from
    /// cache with no network. Never throws; no-op when already lazy.
    /// </summary>
    private void UnloadBackgroundSlots()
    {
        try
        {
            for (int i = 0; i < _tabs.Tabs.Count && i < _slots.Count; i++)
            {
                if (i == _tabs.Active) continue;
                var slot = _slots[i];
                if (slot == null) continue;
                PageTextView? canvas = null;
                try { canvas = FindCanvas(slot); } catch { continue; }
                if (canvas == null) continue;
                if (_restoreSlot == slot) _restoreSlot = null;
                try { _tabs.Tabs[i].ScrollY = canvas.Viewport.Y; } catch { /* headless */ }
                List<View> children;
                try { children = slot.SubViews.ToList(); }
                catch { continue; }
                foreach (var child in children)
                {
                    try { slot.Remove(child); } catch { /* already gone */ }
                    try { child.Dispose(); } catch { }
                }
            }
        }
        catch { /* housekeeping is best-effort */ }
    }

    private void OnTabControlChanged(object? sender, ValueChangedEventArgs<View?> e)
    {
        int idx = e.NewValue == null ? -1 : _tabsView.IndexOf(e.NewValue);
        if (idx < 0 || idx >= _tabs.Tabs.Count) return;
        if (idx != _tabs.Active)
        {
            SaveScroll(Tab);
            _tabs.SwitchTo(idx);
        }
        ActivateTab();
    }

    private void CloseTab()
    {
        int idx = _tabs.Active;
        var tab = _tabs.Tabs[idx];
        tab.CancelLoad();
        var slot = _slots[idx];
        if (_restoreSlot == slot) _restoreSlot = null;
        _slots.RemoveAt(idx);
        try { _tabsView.Remove(slot); }
        catch { /* already gone */ }
        try { slot.Dispose(); } catch { }
        _tabs.CloseTab(idx);
        if (_tabs.Tabs.Count > _slots.Count)
        {
            // Last-tab-close reseeded a home tab: mirror it in the control.
            AddSlotFor(_tabs.Active);
        }
        _tabsView.Value = _slots[_tabs.Active];
        // ValueChanged handler activates (loads home if blank).
    }

    // ---------------- landing page (about:home / about:blank) ----------------

    /// <summary>
    /// Landing-page check (case-insensitive). Home never fetches, never
    /// records history, and renders centered via <see cref="RenderHomeTab"/>.
    /// </summary>
    internal static bool IsHomeUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        string n;
        try { n = WebFetcher.Normalize(input); }
        catch { n = input.Trim(); }
        return n.Equals("about:home", StringComparison.OrdinalIgnoreCase)
            || n.Equals("about:blank", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Centered dummy landing lines for content width <paramref name="w"/>.
    /// Deterministic fixed top pad (no viewport-height dependency, so first
    /// paint before layout and later reflows agree exactly).
    /// </summary>
    internal static List<string> BuildHomeLines(int w)
    {
        w = Math.Clamp(w, 40, 300);
        static string Center(string s, int width)
        {
            if (s.Length >= width) return s[..width];
            return new string(' ', (width - s.Length) / 2) + s;
        }
        const string msg = "Ready To Surf!";
        const string hint = "Type an address above and press Enter.";
        return new List<string>
        {
            "", "", "", "", "", "",
            Center(msg, w),
            "",
            Center(hint, w),
        };
    }

    /// <summary>
    /// UI-thread home application (mirrors ApplyPerceived minus history and
    /// perceive: sets model, draws centered lines, refreshes chrome/focus).
    /// Callers: BeginLoad short-circuit, PaintFirstTab, RebuildSlot, and the
    /// ApplyLoadedPage safety net. Never records history (avoids "New Tab"
    /// spam in :history) and never starts a fetch.
    /// </summary>
    private void RenderHomeTab(TabState tab, string rawUrl)
    {
        string url;
        try { url = WebFetcher.Normalize(rawUrl); }
        catch { url = (rawUrl ?? "").Trim(); }
        if (!IsHomeUrl(url)) url = _homeUrl;
        int w = ContentWidth();
        tab.ClearOverlay();
        tab.Url = url;
        tab.Html = Demo.DemoPages.Home;
        PerceivedPage homePage;
        try { homePage = LayoutPerceiver.Perceive(Demo.DemoPages.Home, url); }
        catch { homePage = tab.Page ?? LayoutPerceiver.Perceive("<html><head><title>New Tab</title></head><body></body></html>", url); }
        tab.Title = homePage.Title.Length > 0 ? homePage.Title : "New Tab";
        tab.Links = homePage.Links;
        tab.Page = homePage;
        tab.LastWidth = w;
        tab.ScrollY = 0;
        tab.IsLoading = false;
        tab.LoadCts = null;

        var slot = SlotFor(tab);
        // Lazy slots (see ApplyPerceived): model is set above either way, but
        // only the active tab draws now; background homes render centered via
        // RebuildSlot when activated.
        if (slot != null && tab == Tab)
        {
            var renderer = NewRenderer(w, url, tab.Links, _pageActions);
            renderer.RenderLines(slot, BuildHomeLines(w));
            _restoreSlot = slot;
            _restoreY = 0;
        }
        if (tab == Tab)
        {
            bool typingNext = false;
            try { typingNext = _urlField.HasFocus && _urlField.Text?.ToString() != tab.Url; }
            catch { }
            _urlField.Text = tab.Url;
            SetCaption(Caption(tab.Title.Length > 0 ? tab.Title : tab.Url));
            RefreshChrome();
            if (!typingNext) FocusCanvas();
        }
        else
        {
            RefreshChrome();
        }
    }

    // ---------------- tab reorder + close-right (browser-style) ----------------

    /// <summary>
    /// Move the active tab by <paramref name="delta"/> (±1). Model, slot
    /// list and Tabs logical order move together; the moved tab stays
    /// active. No-op with a status note at the ends.
    /// </summary>
    private void MoveActiveTab(int delta)
    {
        if (_tabs.Tabs.Count < 2) { SetInfo("only one tab"); return; }
        int from = _tabs.Active;
        if (!_tabs.MoveActive(delta)) { SetInfo(delta < 0 ? "already first tab" : "already last tab"); return; }
        int to = _tabs.Active;
        try
        {
            var slot = _slots[from];
            _slots.RemoveAt(from);
            _slots.Insert(to, slot);
            try { _tabsView.Remove(slot); } catch { /* already gone */ }
            try { _tabsView.InsertTab(to, slot); } catch { /* restore below */ }
            _tabsView.Value = _slots[to];
        }
        catch
        {
            // Model already moved; re-sync views best-effort so chrome never
            // diverges from the model even if the control threw mid-move.
            try { SyncSlotsToModel(); } catch { }
        }
        RefreshChrome();
    }

    /// <summary>
    /// Close every tab to the right of the active tab (context menu).
    /// Anchor never closes; active-in-removed-range falls back to anchor.
    /// </summary>
    private void CloseTabsToRight()
    {
        int anchor = _tabs.Active;
        int doomed = _tabs.Tabs.Count - anchor - 1;
        if (doomed <= 0) { Notify("no tabs to the right"); return; }
        // Capture slots to drop BEFORE mutating the model (indices shift).
        var drop = new List<View>();
        for (int i = anchor + 1; i < _slots.Count && i < _tabs.Tabs.Count; i++)
            drop.Add(_slots[i]);
        if (_restoreSlot != null && drop.Contains(_restoreSlot)) _restoreSlot = null;
        // Cancel model loads for the doomed tabs, then mutate model.
        for (int i = anchor + 1; i < _tabs.Tabs.Count; i++)
            try { _tabs.Tabs[i].CancelLoad(); } catch { }
        int closed = _tabs.CloseTabsToRight(anchor);
        // Mirror removals in slot list + Tabs control (same order as model).
        for (int i = 0; i < closed && _slots.Count > anchor + 1; i++)
        {
            var s = _slots[anchor + 1];
            _slots.RemoveAt(anchor + 1);
            try { _tabsView.Remove(s); } catch { /* already gone */ }
            try { s.Dispose(); } catch { }
        }
        try { _tabsView.Value = _slots[_tabs.Active]; }
        catch { /* header syncs on paint */ }
        RefreshChrome();
        Notify(closed == 1 ? "closed 1 tab to the right" : $"closed {closed} tabs to the right");
    }

    /// <summary>
    /// Best-effort full resync of slot views to model order (move fallback).
    /// Rebuilds Tabs children to match _slots; never throws to callers.
    /// </summary>
    private void SyncSlotsToModel()
    {
        try
        {
            while (_slots.Count < _tabs.Tabs.Count)
            {
                var slot = new View { Title = _tabs.Tabs[_slots.Count].GetLabel() };
                _slots.Add(slot);
                try { _tabsView.Add(slot); } catch { }
            }
            while (_slots.Count > _tabs.Tabs.Count)
            {
                var s = _slots[^1];
                _slots.RemoveAt(_slots.Count - 1);
                try { _tabsView.Remove(s); } catch { }
                try { s.Dispose(); } catch { }
            }
            for (int i = 0; i < _slots.Count; i++)
            {
                try
                {
                    _tabsView.Remove(_slots[i]);
                }
                catch { }
            }
            for (int i = 0; i < _slots.Count; i++)
            {
                try { _tabsView.InsertTab(i, _slots[i]); } catch { try { _tabsView.Add(_slots[i]); } catch { } }
            }
            try { _tabsView.Value = _slots[_tabs.Active]; } catch { }
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Right-click menu for the tab strip. Terminal.Gui Tabs exposes no
    /// per-tab header hit-test, and replicating its header geometry (shared
    /// borders, scroll offset, focus z-order) would risk acting on the wrong
    /// tab — so the menu explicitly names the ACTIVE tab it applies to.
    /// </summary>
    private void ShowTabContextMenu(Terminal.Gui.Input.Mouse m)
    {
        try
        {
            var tab = Tab;
            string label = tab.GetLabel();
            int right = _tabs.Tabs.Count - _tabs.Active - 1;
            var items = new List<MenuItem>
            {
                new MenuItem($"_Close tab '{label}'", "", () => CloseTab()),
                new MenuItem(right > 0
                    ? $"Close tabs to the _right ({right})"
                    : "Close tabs to the _right",
                    "", () => CloseTabsToRight())
                {
                    Enabled = right > 0,
                },
            };
            var menu = new PopoverMenu(items);
            RegisterMenu(menu);
            try
            {
                try { menu.MakeVisible(m.ScreenPosition); }
                catch { menu.MakeVisible(); }
            }
            catch { Notify("menu unavailable"); }
        }
        catch (Exception ex) { Notify($"menu failed: {ex.Message}"); }
    }

    /// <summary>Register a popover with the running app (no-op headless).</summary>
    private void RegisterMenu(PopoverMenu menu)
    {
        try { _app?.Popovers?.Register(menu); }
        catch { /* showing still attempted */ }
    }

    // ---------------- slots ----------------

    private static PageTextView? FindCanvas(View slot) =>
        slot.SubViews.OfType<PageTextView>().FirstOrDefault();

    private void RebuildSlot(TabState tab, int w)
    {
        var slot = SlotFor(tab);
        if (slot == null || tab.Page == null) return;
        // Landing page reflows centered (same helper as first paint/loads),
        // never as a perceived left-aligned flow.
        if (IsHomeUrl(tab.Url))
        {
            SaveScroll(tab);
            tab.LastWidth = w;
            var homeRenderer = NewRenderer(w, tab.Url, tab.Links, _pageActions);
            homeRenderer.RenderLines(slot, BuildHomeLines(w));
            _restoreSlot = slot;
            _restoreY = 0;
            return;
        }
        SaveScroll(tab);
        var renderer = NewRenderer(w, tab.Url, tab.Links, _pageActions);
        var built = renderer.BuildFlow(tab.Page, w);
        tab.LastWidth = w;
        renderer.BuildSlotViews(slot, built);
        _restoreSlot = slot;
        _restoreY = tab.ScrollY;
    }

    private void ReflowActive()
    {
        var tab = Tab;
        if (tab.Page == null || tab.Overlay != TabOverlayKind.None)
        {
            if (tab.Overlay != TabOverlayKind.None) RenderOverlay(tab);
            SyncFindCanvas();
            return;
        }
        RebuildSlot(tab, ContentWidth());
        SyncFindCanvas();
    }

    private void SaveScroll(TabState tab)
    {
        var slot = SlotFor(tab);
        var canvas = slot == null ? null : FindCanvas(slot);
        if (canvas != null)
        {
            try { tab.ScrollY = canvas.Viewport.Y; }
            catch { /* headless */ }
        }
    }

    private void FocusCanvas()
    {
        if (_findField?.HasFocus == true) return;
        try
        {
            var slot = SlotFor(Tab);
            if (slot == null) return;
            FindCanvas(slot)?.SetFocus();
        }
        catch { /* best-effort */ }
    }

    private void BindHotkeys() => _app!.Keyboard.KeyDown += OnBrowserKeyDown;

    private void OnBrowserKeyDown(object? sender, Terminal.Gui.Input.Key key)
    {
        if (key is null || key.Handled || !_win.HasFocus) return;
        switch (HotkeyManager.Map(key))
        {
            case BrowserShortcut.NewTab: key.Handled = true; NewTab(); break;
            case BrowserShortcut.CloseTab: key.Handled = true; CloseTab(); break;
            case BrowserShortcut.NextTab: key.Handled = true; CycleTab(1); break;
            case BrowserShortcut.PrevTab: key.Handled = true; CycleTab(-1); break;
            case BrowserShortcut.FocusUrl: key.Handled = true; _urlField.SetFocus(); break;
            case BrowserShortcut.Reload: key.Handled = true; Reload(); break;
            case BrowserShortcut.MoveTabLeft: key.Handled = true; MoveActiveTab(-1); break;
            case BrowserShortcut.MoveTabRight: key.Handled = true; MoveActiveTab(1); break;
            case BrowserShortcut.Find: key.Handled = true; ShowFindBar(); break;
        }
    }

    private void ShowFindBar()
    {
        if (_findBar != null)
        {
            CloseFindBar();
            return;
        }

        _findField = new TextField { X = 9, Y = 0, Width = Dim.Fill(14), Height = 1 };
        _findStatus = new Label { X = Pos.AnchorEnd(), Y = 0, Width = 12, Height = 1 };
        var hint = new Label { X = 0, Y = 0, Width = 8, Height = 1, Text = "/find> " };
        _findBar = new View
        {
            X = 0, Y = Pos.AnchorEnd(), Width = Dim.Fill(), Height = 1, CanFocus = true, TabStop = TabBehavior.NoStop,
        };
        _findBar.Add(hint, _findField, _findStatus);
        _tabsView.Height = Dim.Fill(1);
        _win.Add(_findBar);

        _findField.Accepting += (_, a) =>
        {
            a.Handled = true;
            FindInCanvas(forward: true);
        };
        _findField.KeyDown += (_, key) =>
        {
            if (key is null || key.Handled) return;
            if (key == Terminal.Gui.Input.Key.Esc)
            {
                key.Handled = true;
                CloseFindBar();
            }
            else if (key == Terminal.Gui.Input.Key.Enter || key == Terminal.Gui.Input.Key.CursorDown)
            {
                key.Handled = true;
                FindInCanvas(forward: true);
            }
            else if (key == Terminal.Gui.Input.Key.Enter.WithShift || key == Terminal.Gui.Input.Key.CursorUp)
            {
                key.Handled = true;
                FindInCanvas(forward: false);
            }
        };
        _findField.TextChanged += (_, _) => ResetFind();
        ResetFind();
        _findField.SetFocus();
        RefreshChrome();
    }

    private PageTextView? CurrentFindCanvas()
    {
        var slot = SlotFor(Tab);
        return slot == null ? null : FindCanvas(slot);
    }

    private void ResetFind()
    {
        foreach (var slot in _slots)
        {
            var canvas = FindCanvas(slot);
            if (canvas == null) continue;
            canvas.IsSelecting = false;
            canvas.FindTextChanged();
            canvas.SetNeedsDraw();
        }
        _findCanvas = CurrentFindCanvas();
        if (_findStatus != null) _findStatus.Text = "";
    }

    private void SyncFindCanvas()
    {
        if (_findBar != null && !ReferenceEquals(_findCanvas, CurrentFindCanvas())) ResetFind();
    }

    private void CloseFindBar()
    {
        if (_findBar == null) return;
        ResetFind();
        var bar = _findBar;
        _findBar = null;
        _findField = null;
        _findStatus = null;
        _findCanvas = null;
        _win.Remove(bar);
        bar.Dispose();
        _tabsView.Height = Dim.Fill();
        FocusCanvas();
        RefreshChrome();
    }

    private void FindInCanvas(bool forward)
    {
        if (_findField == null || _findStatus == null) return;
        SyncFindCanvas();
        var canvas = CurrentFindCanvas();
        string needle = _findField.Text ?? "";
        if (needle.Length == 0)
        {
            ResetFind();
            return;
        }
        if (canvas == null)
        {
            _findStatus.Text = "no page";
            return;
        }
        bool found = SelectFindMatch(canvas, needle, forward, out bool gaveFullTurn);
        if (!found)
        {
            canvas.IsSelecting = false;
            canvas.FindTextChanged();
            canvas.SetNeedsDraw();
        }
        else if (_restoreSlot == SlotFor(Tab))
        {
            _restoreSlot = null;
        }
        _findStatus.Text = found ? (gaveFullTurn ? "hit (wrap)" : "hit") : "no match";
    }

    private static bool SelectFindMatch(PageTextView canvas, string needle, bool forward, out bool wrapped)
    {
        int row = canvas.IsSelecting ? canvas.SelectionStartRow : canvas.CurrentRow;
        int column = canvas.IsSelecting ? canvas.SelectionStartColumn : canvas.CurrentColumn;
        (int Row, int Column, int End)? first = null, last = null, next = null;
        var lines = canvas.GetAllLines();
        for (int y = 0; y < lines.Count; y++)
        {
            string text = string.Concat(lines[y].Select(cell => cell.Grapheme));
            int[] boundaries = System.Globalization.StringInfo.ParseCombiningCharacters(text);
            int offset = 0;
            while (offset <= text.Length - needle.Length)
            {
                int at = text.IndexOf(needle, offset, StringComparison.OrdinalIgnoreCase);
                if (at < 0) break;
                offset = at + 1;
                int start = Array.BinarySearch(boundaries, at);
                int end = at + needle.Length == text.Length ? boundaries.Length
                    : Array.BinarySearch(boundaries, at + needle.Length);
                if (start < 0 || end < 0) continue;
                var hit = (Row: y, Column: start, End: end);
                first ??= hit;
                last = hit;
                bool eligible = forward
                    ? y > row || (y == row && (canvas.IsSelecting ? start > column : start >= column))
                    : y < row || (y == row && start < column);
                if (eligible && (!forward || next == null)) next = hit;
            }
        }
        wrapped = next == null && first != null;
        var match = next ?? (forward ? first : last);
        if (match == null) return false;
        var selected = match.Value;
        canvas.FindTextChanged();
        canvas.IsSelecting = false;
        canvas.InsertionPoint = new Point(selected.End, selected.Row);
        canvas.SelectionStartRow = selected.Row;
        canvas.SelectionStartColumn = selected.Column;
        canvas.IsSelecting = true;
        if (selected.Row < canvas.Viewport.Y || selected.Row >= canvas.Viewport.Bottom)
            canvas.ScrollTo(new Point(canvas.Viewport.X, Math.Max(0, selected.Row - canvas.Viewport.Height + 1)));
        canvas.SetNeedsDraw();
        return true;
    }

    // ---------------- overlays (history / errors) ----------------

    private void ShowHistory()
    {
        var (lines, links) = _engine.HistoryLines(ContentWidth());
        ShowOverlay(TabOverlayKind.History, lines, links, "History");
    }

    private void ShowOverlay(TabOverlayKind kind, List<string> lines, IReadOnlyList<LinkInfo> links, string caption)
    {
        var tab = Tab;
        tab.Overlay = kind;
        tab.OverlaySeq++;
        tab.OverlayCaption = caption;
        tab.OverlayLines = lines;
        tab.OverlayLinks = links;
        tab.Links = links;
        RenderOverlay(tab);
        RefreshChrome();
        FocusCanvas();
    }

    private void ShowErrorOverlay(TabState tab, Exception ex)
    {
        tab.Overlay = TabOverlayKind.Error;
        tab.OverlaySeq++;
        tab.OverlayCaption = "Error";
        tab.OverlayErrorMessage = ex.Message;
        RenderOverlay(tab);
        if (tab == Tab) { RefreshChrome(); FocusCanvas(); }
    }

    private static List<string> BuildErrorLines(string input, string message) => new()
    {
        $"Could not open {input}", "", message, "",
        "Try a full URL (https://example.com),",
        "an offline demo (about:demo), or :history for visited pages.",
    };

    private void RenderOverlay(TabState tab)
    {
        List<string> lines;
        IReadOnlyList<LinkInfo> links;
        string caption;
        if (tab.Overlay == TabOverlayKind.History)
        {
            (lines, links) = _engine.HistoryLines(ContentWidth());
            caption = "History";
            tab.OverlayLines = lines;
            tab.OverlayLinks = links;
            tab.OverlayCaption = caption;
        }
        else if (tab.Overlay == TabOverlayKind.Error)
        {
            string input = tab.OverlayErrorInput;
            if (input.Length == 0) input = tab.Url;
            lines = BuildErrorLines(input, tab.OverlayErrorMessage);
            links = Array.Empty<LinkInfo>();
            caption = "Error";
            tab.OverlayLines = lines;
            tab.OverlayLinks = links;
            tab.OverlayCaption = caption;
        }
        else return;

        var slot = SlotFor(tab);
        tab.Links = links;
        // Lazy slots (see ApplyPerceived): background overlays keep model
        // state only; the canvas builds when the tab activates.
        if (slot == null || tab != Tab) return;
        var renderer = NewRenderer(ContentWidth(), tab.Url, links, _pageActions);
        renderer.RenderLines(slot, lines);
        if (tab == Tab)
        {
            SetCaption(Caption(caption));
            _urlField.Text = tab.Url;
        }
    }

    // ---------------- form results ----------------

    internal void SubmitPost(string actionUrl, List<FieldData> fields, List<FileData> files, bool multipart)
    {
        var tab = Tab;
        BeginLoad(tab, async ct =>
        {
            using var content = FormSubmit.BuildPostContent(fields, files, multipart);
            var posted = await _engine.Poster.PostFormAsync(actionUrl, content, ct).ConfigureAwait(false);
            return (OpenResultDoc(posted.Body, posted.FinalUrl, posted.ContentType), posted.FinalUrl);
        }, pushHistory: true, label: actionUrl);
    }

    internal void OpenResult(string body, string finalUrl, string? contentType)
    {
        var tab = Tab;
        string doc = OpenResultDoc(body, finalUrl, contentType);
        BeginLoad(tab, _ => Task.FromResult((doc, finalUrl)), pushHistory: true);
    }

    private static string OpenResultDoc(string body, string finalUrl, string? contentType)
    {
        bool html = (contentType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true)
                 || body.TrimStart().StartsWith("<", StringComparison.Ordinal);
        return html ? body
            : "<html><head><title>" + WebUtility.HtmlEncode(finalUrl) + "</title></head>"
            + "<body><pre>" + WebUtility.HtmlEncode(body) + "</pre></body></html>";
    }

    internal void Notify(string message) => SetInfo(message);

    internal void PreviewLink(string? url)
    {
        try
        {
            if (_win == null) return;
            // Idempotent: hover position reports arrive on EVERY mousemove, and
            // each title change pays a window-border redraw. Skipping the write
            // when the title already shows this preview turns a per-mousemove
            // redraw storm (very visible while wheel-scrolling) into a compare.
            // Stateless on purpose: after navigation SetCaption changes the
            // title, so the next hover over the same URL still refreshes.
            string target = url == null ? _pageCaption : "→ " + url;
            if (_win.Title == target) return;
            _win.Title = target;
        }
        catch { /* best-effort */ }
    }

    internal void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) { Notify("nothing to copy"); return; }
        try
        {
            bool ok = false;
            try
            {
                var clipboard = _app?.Clipboard;
                ok = clipboard != null && clipboard.TrySetClipboardData(text);
            }
            catch { ok = false; }
            Notify(ok ? $"copied {text.Length} chars" : "clipboard unavailable");
        }
        catch (Exception ex) { Notify($"copy failed: {ex.Message}"); }
    }

    internal void TakeScreenshot()
    {
        try
        {
            string dir = Path.Combine(_engine.DataDir, "screenshots");
            Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string file = Path.Combine(dir, $"texbowser-{stamp}.txt");
            string body;
            try
            {
                if (Tab.Html.Length > 0)
                    body = _engine.RenderHtml(Tab.Html, Tab.Url, ContentWidth(), recordHistory: false).Text;
                else
                    body = $"{Tab.Url}\n(no page loaded)\n";
            }
            catch (Exception ex) { body = $"screenshot failed to render: {ex.Message}\n"; }
            string header = $"TexBowser screenshot\nURL: {Tab.Url}\nTitle: {Tab.Title}\nTaken: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n";
            File.WriteAllText(file, header + body);
            Notify($"screenshot saved: {file}");
        }
        catch (Exception ex) { Notify($"screenshot failed: {ex.Message}"); }
    }

    private void SetInfo(string message) => SetCaption(message);

    private void SetCaption(string caption)
    {
        _pageCaption = caption;
        _win.Title = caption;
    }

    // ---------------- chrome ----------------

    /// <summary>
    /// Build Row 0 address chrome. Single source of truth for layout AND for
    /// the headless chrome test: [<][>][Reload][url][Go][Hist]. The tab strip
    /// (+/x) lives in Row 1 — see <see cref="BuildTabRowButtons"/>.
    /// Url reserves 16 right cols: [Go 6][1][Hist 8][1] to the window edge.
    /// </summary>
    internal View[] BuildNavRow(out Button goBtn, out Button histBtn)
    {
        // Terminal.Gui v2 Button defaults to ShadowStyle=Opaque, which reserves
        // a 1-col right + 1-row bottom margin for the shadow. At Height=1 that
        // leaves Viewport.Height=0, so the label never draws and the button
        // looks missing entirely (only the url TextField, whose default shadow
        // is null, stayed visible). ShadowStyle=null removes the margin so a
        // single-row button keeps its full viewport and stays visible.
        _backBtn = new Button { Text = "<", X = 0, Y = 0, Width = 5, Height = 1, ShadowStyle = null };
        _fwdBtn = new Button { Text = ">", X = 6, Y = 0, Width = 5, Height = 1, ShadowStyle = null };
        _reloadBtn = new Button { Text = "Reload", X = 12, Y = 0, Width = 10, Height = 1, ShadowStyle = null };
        _urlField = new TextField { Id = "url", X = 23, Y = 0, Width = Dim.Fill(16), Height = 1 };
        // NOTE: Pos.AnchorEnd() is right-edge aligned (X = W-width); subtract
        // the desired right-edge margin so every button stays fully visible.
        // Hist margin 0 (right=W); Go margin 9 (Hist 8 + 1 gap, right=W-9).
        goBtn = new Button { Text = "Go", X = Pos.AnchorEnd() - 9, Y = 0, Width = 6, Height = 1, ShadowStyle = null };
        histBtn = new Button { Text = "Hist", X = Pos.AnchorEnd(), Y = 0, Width = 8, Height = 1, ShadowStyle = null };
        return new View[] { _backBtn, _fwdBtn, _reloadBtn, _urlField, goBtn, histBtn };
    }

    /// <summary>
    /// Build Row 1 tab-strip buttons: [+] new tab next to the tabs, [x] close
    /// active tab at the top-right. Siblings of (never children of) the Tabs
    /// control, Y=1. Tabs itself uses Width=Fill(<see cref="TabStripReserve"/>)
    /// so [Tabs 1-gap + 1-gap x] tile the row with 1-col gaps.
    /// </summary>
    internal View[] BuildTabRowButtons(out Button newTabBtn, out Button closeTabBtn)
    {
        // Same single-row shadow trap as the address chrome: null keeps the
        // 1-row viewport drawable.
        newTabBtn = new Button { Text = "+", X = Pos.AnchorEnd() - 6, Y = 1, Width = 5, Height = 1, ShadowStyle = null };
        closeTabBtn = new Button { Text = "x", X = Pos.AnchorEnd(), Y = 1, Width = 5, Height = 1, ShadowStyle = null };
        return new View[] { newTabBtn, closeTabBtn };
    }

    /// <summary>Refresh nav buttons, url box, caption and tab titles from model state.</summary>
    private void RefreshChrome()
    {
        SyncFindCanvas();
        var tab = Tab;
        try { _backBtn.Enabled = tab.CanGoBack; } catch { }
        try { _fwdBtn.Enabled = tab.CanGoForward; } catch { }
        try { _reloadBtn.Text = tab.IsLoading ? "Stop" : "Reload"; } catch { }
        try
        {
            // Never clobber an address the user is actively typing: background
            // tab loads refresh titles/captions but leave the focused box alone.
            if (_urlField.Text?.ToString() != tab.Url && !_urlField.HasFocus)
                _urlField.Text = tab.Url;
        }
        catch { }
        for (int i = 0; i < _slots.Count && i < _tabs.Tabs.Count; i++)
        {
            try { _slots[i].Title = _tabs.Tabs[i].GetLabel(); }
            catch { /* headless */ }
        }
        SetCaption(tab.Overlay != TabOverlayKind.None
            ? Caption(tab.OverlayCaption)
            : Caption(tab.Title.Length > 0 ? tab.Title : tab.Url));
    }

    private int ContentWidth()
    {
        try
        {
            var slot = _slots.Count > _tabs.Active ? _slots[_tabs.Active] : null;
            var canvas = slot == null ? null : FindCanvas(slot);
            if (canvas != null && canvas.Frame.Width > 10) return canvas.Frame.Width;
            if (_win != null)
            {
                int ww = _win.Frame.Width;
                if (ww > 10) return Math.Clamp(ww - 2, 40, 300);
            }
            return Math.Clamp(Console.WindowWidth, 40, 300);
        }
        catch { return 100; }
    }

    private static string Caption(string name) =>
        string.IsNullOrWhiteSpace(name) ? "TexBowser" : $"{name} — TexBowser";
}

