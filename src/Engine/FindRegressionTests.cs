using System.Reflection;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TexBowser.Layout;
using TexBowser.Render;
using TexBowser.Ui;

namespace TexBowser.Engine;

public static class FindRegressionTests
{
    public static int Run()
    {
        int failures = 0;
        foreach (var (name, test) in new (string, Action)[]
        {
            ("central shortcut mapping", TestMapping),
            ("show, layout, search, close and repeat", TestFindFlow),
            ("query change, miss and empty query", TestQueryChanges),
            ("canvas replacement, reflow and focus", TestReplacement),
            ("tab switch and shortcuts", TestTabs),
            ("no canvas and repeated close", TestNoCanvas),
        })
        {
            try
            {
                test();
                Console.WriteLine($"  [PASS] find: {name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"  [FAIL] find: {name}: {ex.GetBaseException().Message}");
            }
        }
        return failures;
    }

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    private static void TestMapping()
    {
        foreach (var (key, expected) in new (Key, BrowserShortcut?)[]
        {
            (Key.T.WithCtrl, BrowserShortcut.NewTab),
            (Key.W.WithCtrl, BrowserShortcut.CloseTab),
            (Key.Tab.WithCtrl, BrowserShortcut.NextTab),
            (Key.Tab.WithCtrl.WithShift, BrowserShortcut.PrevTab),
            (Key.L.WithCtrl, BrowserShortcut.FocusUrl),
            (Key.R.WithCtrl, BrowserShortcut.Reload),
            (Key.F.WithCtrl, BrowserShortcut.Find),
            (Key.CursorLeft.WithCtrl.WithShift, BrowserShortcut.MoveTabLeft),
            (Key.CursorRight.WithCtrl.WithShift, BrowserShortcut.MoveTabRight),
            (Key.F, null),
            (Key.F.WithAlt.WithCtrl, null),
            (Key.F.WithCtrl.WithShift, null),
            (Key.CursorLeft.WithCtrl, null),
            (Key.A.WithCtrl, null),
            (Key.Enter, null),
            (Key.Esc, null),
            (null!, null),
        })
        {
            Check(HotkeyManager.Map(key) == expected, $"mapping {key}");
            Check(KeyboardShortcuts.Map(key) == expected, $"compatibility mapping {key}");
        }
    }

    private static void TestFindFlow()
    {
        using var f = new Fixture();
        var canvas = f.Canvas;
        int closedHeight = f.Tabs.Frame.Height;
        f.Shortcut(Key.F.WithCtrl);
        var bar = f.Get<View>("_findBar");
        f.Window.Layout();
        Check(bar.SuperView == f.Window && !f.Slot.SubViews.Contains(bar), "bar must be a window sibling");
        Check(f.Tabs.Frame.Height == closedHeight - 1, "open bar reserves one row");
        Check(f.Tabs.Frame.Bottom == bar.Frame.Top && bar.Frame.Bottom == f.Window.Viewport.Height,
            "bar must tile bottom row without overlap");
        Check(f.Field.Frame.Right <= f.Status.Frame.Left && f.Status.Frame.Right <= bar.Viewport.Width,
            "query and status must not overlap");
        Check(f.Field.HasFocus, "show focuses query");
        f.Field.Text = "alpha";
        f.Key(Key.Enter);
        Check(canvas.SelectedText == "alpha" && canvas.SelectionStartColumn == 0, "first hit");
        f.Key(Key.Enter);
        Check(canvas.SelectionStartColumn == 11, "Enter advances exactly once");
        f.Key(Key.Enter.WithShift);
        Check(canvas.SelectionStartColumn == 0, "Shift+Enter goes backward");
        f.Key(Key.Enter.WithShift);
        Check(canvas.SelectionStartColumn == 11, $"previous wraps: start={canvas.SelectionStartColumn}, selected='{canvas.SelectedText}', status='{f.Status.Text}'");
        f.Key(Key.Enter);
        Check(canvas.SelectionStartColumn == 0, "next wraps");
        Check(f.Field.HasFocus && f.Field.Text == "alpha", "search preserves typing focus and query");
        f.Window.Width = 60;
        f.Window.Height = 15;
        f.Window.Layout();
        Check(f.Tabs.Frame.Bottom == bar.Frame.Top && bar.Frame.Bottom == f.Window.Viewport.Height,
            "resize keeps reserved bottom row");
        f.Key(Key.Esc);
        f.Window.Layout();
        Check(f.Get<View?>("_findBar") == null && !f.Window.SubViews.Contains(bar), "Esc removes bar");
        Check(f.Tabs.Frame.Bottom == f.Window.Viewport.Height, "close restores page height");
        Check(canvas.SelectedLength == 0 && canvas.HasFocus, "close clears match and returns focus");
        for (int i = 0; i < 3; i++)
        {
            f.Shortcut(Key.F.WithCtrl);
            f.Field.Text = "beta";
            f.Key(Key.Enter);
            Check(canvas.SelectedText == "beta", "repeat open searches live canvas");
            f.Shortcut(Key.F.WithCtrl);
            Check(f.Get<View?>("_findBar") == null, "repeat Ctrl+F closes");
        }
    }

    private static void TestQueryChanges()
    {
        using var f = new Fixture();
        f.Shortcut(Key.F.WithCtrl);
        f.Field.Text = "alpha";
        f.Key(Key.Enter);
        f.Field.Text = "beta";
        Check(f.Canvas.SelectedLength == 0 && f.Status.Text == "", "change clears selection and status immediately");
        f.Key(Key.Enter);
        Check(f.Canvas.SelectedText == "beta", "changed query finds new match");
        f.Field.Text = "absent";
        f.Canvas.FindNextText("alpha", out _);
        Check(f.Canvas.SelectedLength > 0, "seed stale selection before miss");
        f.Key(Key.Enter);
        Check(f.Status.Text == "no match" && f.Canvas.SelectedLength == 0, "miss clears stale selection");
        f.Field.Text = "ALPHA";
        f.Key(Key.Enter);
        Check(f.Canvas.SelectedText == "alpha", $"case insensitive match after miss: selected='{f.Canvas.SelectedText}', status='{f.Status.Text}'");
        f.Field.Text = "";
        f.Key(Key.Enter);
        Check(f.Status.Text == "" && f.Canvas.SelectedLength == 0, "empty query clears selection");
        f.Field.Text = "beta";
        f.Key(Key.Enter);
        Check(f.Canvas.SelectedText == "beta", "find resumes after empty query");
    }

    private static void TestReplacement()
    {
        using var f = new Fixture();
        f.Shortcut(Key.F.WithCtrl);
        var field = f.Field;
        var old = f.Canvas;
        field.Text = "alpha";
        f.Key(Key.Enter);
        f.Load("<p>replacement alpha body with enough text to render</p>");
        Check(!ReferenceEquals(old, f.Canvas), "page application replaces canvas");
        Check(ReferenceEquals(field, f.Field) && field.HasFocus, "page application preserves find field and focus");
        Check(f.Status.Text == "", "replacement clears stale status");
        f.Key(Key.Enter);
        Check(f.Canvas.SelectedText == "alpha", "find resolves replacement canvas");
        Check(f.Get<View?>("_restoreSlot") == null, "search cancels pending scroll restore");
        old = f.Canvas;
        f.Call("ReflowActive");
        f.Window.Layout();
        Check(!ReferenceEquals(old, f.Canvas) && field.HasFocus, "reflow preserves find focus");
        f.Key(Key.Enter);
        Check(f.Canvas.SelectedText == "alpha", "find resolves reflowed canvas");
        f.Call("RenderHomeTab", f.Model.Current, "about:home");
        Check(field.HasFocus, "home completion preserves find focus");
        field.Text = "Ready";
        f.Key(Key.Enter);
        Check(f.Canvas.SelectedText == "Ready", "home canvas remains searchable");
        f.Call("ShowErrorOverlay", f.Model.Current, new InvalidOperationException("alpha error"));
        Check(field.HasFocus, "error overlay preserves find focus");
        field.Text = "alpha";
        f.Key(Key.Enter);
        Check(f.Canvas.SelectedText == "alpha", "overlay replacement is searchable");
    }

    private static void TestTabs()
    {
        using var f = new Fixture();
        f.Shortcut(Key.T.WithCtrl);
        Check(f.Model.Tabs.Count == 2 && f.Model.Active == 1, "Ctrl+T dispatches new tab");
        f.Load("<p>second beta page with enough words to render</p>");
        f.Shortcut(Key.F.WithCtrl);
        f.Field.Text = "beta";
        f.Key(Key.Enter);
        Check(f.Canvas.SelectedText == "beta", "second tab match");
        var field = f.Field;
        f.Shortcut(Key.Tab.WithCtrl.WithShift);
        Check(f.Model.Active == 0, "previous tab dispatch");
        Check(ReferenceEquals(field, f.Field) && field.Text == "beta", "tab change retains query");
        Check(f.Status.Text == "", "tab change clears status");
        f.Field.SetFocus();
        f.Key(Key.Enter);
        Check(f.Canvas.SelectedText == "beta", "search rebuilt first tab");
        f.Shortcut(Key.Tab.WithCtrl);
        Check(f.Model.Active == 1, "next tab dispatch");
        f.Field.SetFocus();
        f.Key(Key.Enter);
        Check(f.Canvas.SelectedText == "beta", "search rebuilt second tab");
        f.Shortcut(Key.CursorLeft.WithCtrl.WithShift);
        Check(f.Model.Active == 0, "move left dispatch");
        f.Shortcut(Key.CursorRight.WithCtrl.WithShift);
        Check(f.Model.Active == 1, "move right dispatch");
        f.Shortcut(Key.L.WithCtrl);
        Check(f.Get<TextField>("_urlField").HasFocus, "Ctrl+L dispatch");
        f.Shortcut(Key.W.WithCtrl);
        Check(f.Model.Tabs.Count == 1, "Ctrl+W dispatch");
        f.Field.SetFocus();
        f.Key(Key.Esc);
        Check(f.Get<View?>("_findBar") == null, "close after tab disposal");
    }

    private static void TestNoCanvas()
    {
        using var f = new Fixture();
        foreach (var child in f.Slot.SubViews.ToList())
        {
            f.Slot.Remove(child);
            child.Dispose();
        }
        f.Shortcut(Key.F.WithCtrl);
        f.Field.Text = "alpha";
        f.Key(Key.Enter);
        Check(f.Status.Text == "no page", "no canvas reports status");
        f.Shortcut(Key.F.WithCtrl);
        f.Call("CloseFindBar");
        Check(f.Get<View?>("_findBar") == null, "close is safe without canvas and idempotent");
    }

    private sealed class Fixture : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"texbowser-find-{Guid.NewGuid():N}");
        private readonly BrowserEngine _engine;
        private readonly IApplication _application = Application.Create();
        public BrowserApp Browser { get; }
        public Window Window { get; } = new() { Width = 100, Height = 30 };
        public Tabs Tabs { get; } = new() { X = 0, Y = 1, Width = Dim.Fill(BrowserApp.TabStripReserve), Height = Dim.Fill() };
        public TabsModel Model => Get<TabsModel>("_tabs");
        public View Slot => Get<List<View>>("_slots")[Model.Active];
        public PageTextView Canvas => Slot.SubViews.OfType<PageTextView>().Single();
        public TextField Field => Get<TextField>("_findField");
        public Label Status => Get<Label>("_findStatus");

        public Fixture()
        {
            Directory.CreateDirectory(_dir);
            _engine = new BrowserEngine(_dir);
            Browser = new BrowserApp(_engine);
            Set("_app", _application);
            Set("_win", Window);
            Set("_tabsView", Tabs);
            var actionsType = typeof(BrowserApp).GetNestedType("PageActions", BindingFlags.NonPublic)!;
            var actions = Activator.CreateInstance(actionsType)!;
            actionsType.GetField("App")!.SetValue(actions, Browser);
            Set("_pageActions", actions);
            Window.Add(Browser.BuildNavRow(out _, out _));
            Window.Add(Tabs);
            Window.Add(Browser.BuildTabRowButtons(out _, out _));
            var changed = (EventHandler<ValueChangedEventArgs<View?>>)typeof(BrowserApp)
                .GetMethod("OnTabControlChanged", Private)!.CreateDelegate(typeof(EventHandler<ValueChangedEventArgs<View?>>), Browser);
            Tabs.ValueChanged += changed;
            Call("BindHotkeys");
            Call("AddSlotFor", 0);
            Tabs.Value = Slot;
            Load("<p>alpha beta alpha</p>");
            Canvas.SetDocument(new List<string> { "alpha beta alpha" }, Array.Empty<LinkInfo>());
            Window.Layout();
            Canvas.SetFocus();
        }

        public T Get<T>(string name) => (T)typeof(BrowserApp).GetField(name, Private)!.GetValue(Browser)!;
        private void Set(string name, object value) => typeof(BrowserApp).GetField(name, Private)!.SetValue(Browser, value);
        public void Call(string name, params object?[] args) => typeof(BrowserApp).GetMethod(name, Private)!.Invoke(Browser, args);

        public void Load(string body)
        {
            string html = "<html><body><main>" + body + "</main></body></html>";
            const string url = "https://example.com/find";
            var page = LayoutPerceiver.Perceive(html, url);
            var actions = Get<IBrowserActions>("_pageActions");
            var flow = new ViewRenderer(86, url, page.Links, actions).BuildFlow(page, 86);
            Call("ApplyPerceived", Model.Current, html, url, page, flow, 86, false);
            Window.Layout();
        }

        public void Shortcut(Key key)
        {
            _application.Keyboard.RaiseKeyDownEvent(key);
            Check(key.Handled, $"browser shortcut not handled: {key}");
        }

        public void Key(Key key)
        {
            Window.NewKeyDownEvent(key);
            Check(key.Handled, $"find key not handled: {key}");
        }

        public void Dispose()
        {
            Window.Dispose();
            _application.Dispose();
            _engine.Dispose();
            Directory.Delete(_dir, true);
        }
    }
}
