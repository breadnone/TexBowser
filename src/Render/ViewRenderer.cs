using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TexBowser.Layout;
using TexBowser.Net;

namespace TexBowser.Render;

/// <summary>Callbacks from live page views back into the browser chrome.</summary>
public interface IBrowserActions
{
    void Navigate(string url);
    void SubmitPost(string actionUrl, List<FieldData> fields, List<FileData> files, bool multipart);
    void OpenResult(string body, string finalUrl, string? contentType);
    void Notify(string message);
    /// <summary>Hover URL preview; null restores the status line.</summary>
    void PreviewLink(string? url);
    /// <summary>Copy text to OS clipboard (generic, no site-specific handling).</summary>
    void CopyToClipboard(string text);
    /// <summary>Save current page text to a file (generic screenshot).</summary>
    void TakeScreenshot();
}

/// <summary>
/// Builds the page as ONE canvas (<see cref="PageTextView"/>) holding every
/// rendered text line, plus live controls for forms only. The old design made
/// one TextView per paragraph plus one Label per link marker — hundreds of
/// views on big pages, one focus stop each, all redrawn on every scroll tick.
/// The canvas draws only visible rows, scrolls natively, owns a single Tab
/// stop for all prose, and hit-tests "[n]" markers itself, so scrolling stays
/// smooth and focus can never get lost among paragraph views.
/// Layout geometry (regions, column widths, stacked fallbacks, rules) mirrors
/// <see cref="PageRenderer"/> exactly; only forms differ (live controls over
/// blank placeholder rows instead of headless descriptor text).
/// </summary>
public sealed class ViewRenderer
{
    private readonly int _width;
    private readonly string _baseUrl;
    private readonly IReadOnlyList<LinkInfo> _links;
    private readonly IBrowserActions _actions;

    /// <summary>
    /// Running application for popover registration. Null in headless
    /// selftest (menus then degrade to a "menu unavailable" note instead of
    /// throwing). Never the static Application model — mixing instance and
    /// static crashes the process on Terminal.Gui v2.
    /// </summary>
    public IApplication? App { get; set; }

    public ViewRenderer(int width, string baseUrl, IReadOnlyList<LinkInfo> links, IBrowserActions actions)
    {
        _width = Math.Max(20, width);
        _baseUrl = baseUrl;
        _links = links;
        _actions = actions;
    }

    // ---------------- page ----------------

    /// <summary>Last flow built by RenderInto (test hook for geometry checks).</summary>
    internal Flow? LastFlow { get; private set; }

    /// <summary>
    /// Render a perceived page into <paramref name="slot"/>: a single canvas
    /// plus live form controls. Returns the document line count.
    /// </summary>
    public int RenderInto(View slot, PerceivedPage page)
    {
        var flow = BuildFlow(page, Math.Clamp(_width, 40, 300));
        LastFlow = flow;
        BuildSlotViews(slot, flow);
        return flow.Lines.Count;
    }

    /// <summary>Pure line/spec walk (no views): safe off the UI thread.</summary>
    internal Flow BuildFlow(PerceivedPage page, int width)
    {
        int w = Math.Clamp(width, 40, 300);
        var flow = new Flow();
        FlowPage(flow, page, w);
        return flow;
    }

    /// <summary>Instantiate canvas + controls into a slot (UI thread).</summary>
    internal void BuildSlotViews(View slot, Flow flow)
    {
        foreach (var child in slot.SubViews.ToList())
        {
            slot.Remove(child);
            child.Dispose();
        }
        var canvas = new PageTextView(_actions)
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };
        canvas.LinkMenu += (url, m) => ShowLinkContextMenu(url, m);
        canvas.PageMenu += (tv, m) => ShowContextMenu(tv, m);
        canvas.MouseLeave += (_, _) => _actions.PreviewLink(null);
        canvas.SetDocument(flow.Lines, _links);
        slot.Add(canvas);
        BuildControls(canvas, flow);
    }

    /// <summary>Auxiliary line pages (history, errors): one canvas, clickable.</summary>
    public int RenderLines(View slot, List<string> lines)
    {
        var clean = lines.Select(l => l.Length <= _width ? l : l[.._width]).ToList();
        foreach (var child in slot.SubViews.ToList())
        {
            slot.Remove(child);
            child.Dispose();
        }
        var canvas = new PageTextView(_actions)
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };
        canvas.LinkMenu += (url, m) => ShowLinkContextMenu(url, m);
        canvas.PageMenu += (tv, m) => ShowContextMenu(tv, m);
        canvas.MouseLeave += (_, _) => _actions.PreviewLink(null);
        canvas.SetDocument(clean, _links);
        slot.Add(canvas);
        return clean.Count;
    }

    // ---------------- flow walk (mirrors PageRenderer geometry) ----------------

    /// <summary>Accumulated canvas lines + deferred live-control specs.</summary>
    internal sealed class Flow
    {
        public readonly List<string> Lines = new();
        public readonly List<ControlSpec> Specs = new();
        private int _formSeq;

        public int NewFormIndex() => _formSeq++;

        /// <summary>Append pre-wrapped lines (trailing empties stripped, like AddLabel).</summary>
        public int AddText(IReadOnlyList<string> lines, int w)
        {
            int n = lines.Count;
            while (n > 0 && (lines[n - 1].Length == 0)) n--;
            for (int i = 0; i < n; i++)
            {
                string l = lines[i];
                Lines.Add(l.Length <= w ? l : l[..w]);
            }
            return n;
        }

        public void AddBlank(int n)
        {
            for (int i = 0; i < n; i++) Lines.Add("");
        }
    }

    internal abstract record ControlSpec(FormData Form, int FormIndex, int X, int Y, int W);
    internal sealed record FieldSpec(FormData Form, int FormIndex, FormControl Ctrl, int X, int Y, int W)
        : ControlSpec(Form, FormIndex, X, Y, W);
    internal sealed record HiddenSpec(FormData Form, int FormIndex, FormControl Ctrl)
        : ControlSpec(Form, FormIndex, 0, 0, 0);
    internal sealed record OptionSpec(FormData Form, int FormIndex, List<FormControl>? Group,
        FormControl? Select, string Prompt, List<string> Options, int Selected, bool Disabled,
        int X, int Y, int W) : ControlSpec(Form, FormIndex, X, Y, W);
    internal sealed record ButtonSpec(FormData Form, int FormIndex, FormControl Ctrl, bool IsReset,
        int X, int Y, int BW) : ControlSpec(Form, FormIndex, X, Y, BW);

    private void FlowPage(Flow f, PerceivedPage page, int w)
    {
        string host;
        try
        {
            host = new Uri(page.BaseUrl).Host;
            if (host.Length == 0) host = page.BaseUrl;
        }
        catch { host = page.BaseUrl; }

        f.Lines.Add(new string('═', w));
        f.AddText(TextUtil.Wrap(TextConcat.HeaderLine(host, page.Signature, page.DetectionSummary, w), w), w);
        if (!string.IsNullOrWhiteSpace(page.Title))
            f.AddText(TextUtil.Wrap(page.Title, w), w);
        f.Lines.Add(new string('═', w));

        if (page.HasHeader && page.Header != null)
        {
            f.Lines.Add(TextUtil.Rule(w));
            FlowNode(f, page.Header, 0, f.Lines.Count, w);
        }

        if (page.HasNav)
        {
            bool anyNav = false;
            if (page.NavLinks.Count > 0)
            {
                f.Lines.Add(TextUtil.Rule(w));
                var items = page.NavLinks.Select(l => TextConcat.LinkLabel(l.Text, l.Index)).ToList();
                f.AddText(TextUtil.WrapStrip(items, w), w);
                anyNav = true;
            }
            foreach (var form in page.NavForms)
            {
                if (!anyNav) { f.Lines.Add(TextUtil.Rule(w)); anyNav = true; }
                FlowNode(f, form, 0, f.Lines.Count, w);
            }
        }

        bool sideBySide = page.HasAside && w >= PageRenderer.CollapseBelow;
        if (sideBySide)
        {
            int mainW = (int)Math.Round((w - TextUtil.ColSep.Length) * page.AsideRatio);
            mainW = Math.Clamp(mainW, 20, w - 20 - TextUtil.ColSep.Length);
            int asideW = w - TextUtil.ColSep.Length - mainW;
            var main = new Flow();
            FlowNode(main, page.Main, 0, 0, mainW);
            var aside = new Flow();
            for (int i = 0; i < page.Asides.Count; i++)
            {
                if (i > 0) aside.Lines.Add("╌╌╌");
                FlowNode(aside, page.Asides[i], 0, aside.Lines.Count, asideW);
            }
            int cur = f.Lines.Count;
            f.Lines.Add(TextUtil.Rule(w));
            cur++;
            f.Lines.AddRange(TextUtil.RenderColumnGrid(
                new[] { (IReadOnlyList<string>)main.Lines, aside.Lines },
                new[] { mainW, asideW }));
            OffsetSpecs(f, main, 0, cur);
            OffsetSpecs(f, aside, mainW + TextUtil.ColSep.Length, cur);
        }
        else
        {
            var main = new Flow();
            FlowNode(main, page.Main, 0, 0, w);
            if (main.Lines.Count > 0)
            {
                int cur = f.Lines.Count;
                f.Lines.Add(TextUtil.Rule(w));
                f.Lines.AddRange(main.Lines);
                OffsetSpecs(f, main, 0, cur + 1);
            }
            if (page.HasAside)
            {
                foreach (var aside in page.Asides)
                {
                    f.Lines.Add(TextUtil.Rule(w));
                    FlowNode(f, aside, 0, f.Lines.Count, w);
                }
            }
        }

        if (page.HasFooter && page.Footer != null)
        {
            f.Lines.Add(TextUtil.Rule(w));
            FlowNode(f, page.Footer, 0, f.Lines.Count, w);
        }
    }

    private static void OffsetSpecs(Flow target, Flow source, int dx, int dy)
    {
        foreach (var s in source.Specs)
            target.Specs.Add(s with { X = s.X + dx, Y = s.Y + dy });
    }

    internal int FlowNode(Flow f, LayoutNode node, int x, int y, int w)
    {
        // NOTE: y must equal f.Lines.Count on entry; sub-flows manage their own.
        return node switch
        {
            TextNode t => f.AddText(PageRenderer.BlockLines(t.Text, w), w),
            DataTableNode d => FlowTable(f, d, w),
            RowNode r => FlowRow(f, r, x, y, w),
            StackNode s => FlowStack(f, s, x, y, w),
            FormNode fn => FlowForm(f, fn.Form, x, y, w),
            _ => 0,
        };
    }

    private int FlowStack(Flow f, StackNode stack, int x, int y, int w)
    {
        // Mirror PageRenderer.RenderStack: blank line BETWEEN non-empty children.
        int start = f.Lines.Count;
        var parts = new List<Flow>();
        foreach (var child in stack.Children)
        {
            var sub = new Flow();
            FlowNode(sub, child, x, 0, w);
            if (sub.Lines.Count == 0) continue;
            parts.Add(sub);
        }
        for (int i = 0; i < parts.Count; i++)
        {
            if (i > 0) f.Lines.Add("");
            int cur = f.Lines.Count;
            f.Lines.AddRange(parts[i].Lines);
            OffsetSpecs(f, parts[i], 0, cur);
        }
        _ = y;
        return f.Lines.Count - start;
    }

    private int FlowRow(Flow f, RowNode row, int x, int y, int w)
    {
        var fracs = row.Cells.Select(c => c.Fraction).ToArray();
        double sum = fracs.Sum();
        if (sum <= 0) fracs = fracs.Select(_ => 1.0 / fracs.Length).ToArray();
        else fracs = fracs.Select(v => v / sum).ToArray();

        int[] widths = TextUtil.PixelWidths(fracs, w - TextUtil.ColSep.Length * (fracs.Length - 1));
        if (widths.Any(v => v < PageRenderer.MinColWidth) || widths.Sum() <= 0)
        {
            int start = f.Lines.Count;
            for (int i = 0; i < row.Cells.Count; i++)
            {
                f.Lines.Add(TextUtil.Rule(w, TextConcat.ColLabel(i, row.Cells.Count)));
                FlowNode(f, row.Cells[i].Content, x, f.Lines.Count, w);
            }
            _ = y;
            return f.Lines.Count - start;
        }

        int off = 0;
        var cellFlows = new List<Flow>();
        for (int i = 0; i < row.Cells.Count; i++)
        {
            var sub = new Flow();
            FlowNode(sub, row.Cells[i].Content, 0, 0, widths[i]);
            cellFlows.Add(sub);
            off += widths[i] + TextUtil.ColSep.Length;
        }
        int cur = f.Lines.Count;
        f.Lines.AddRange(TextUtil.RenderColumnGrid(
            cellFlows.Select(c => (IReadOnlyList<string>)c.Lines).ToList(), widths));
        off = 0;
        for (int i = 0; i < row.Cells.Count; i++)
        {
            OffsetSpecs(f, cellFlows[i], x + off, cur);
            off += widths[i] + TextUtil.ColSep.Length;
        }
        _ = y;
        return f.Lines.Count - cur;
    }

    private int FlowTable(Flow f, DataTableNode node, int w)
    {
        var t = node.Table;
        int cols = Math.Max(t.Headers.Count, t.Rows.Count == 0 ? 0 : t.Rows.Max(r => r.Count));
        int start = f.Lines.Count;
        f.Lines.Add(TextUtil.Rule(w, TextConcat.TableTitle(t.Rows.Count, cols)));
        f.Lines.AddRange(TextUtil.RenderDataTable(t.Headers, t.Rows, w));
        return f.Lines.Count - start;
    }

    // ---------------- forms: text to flow, controls deferred ----------------

    private sealed class BoundForm
    {
        public required FormData Spec;
        public List<Func<ControlState>> Getters = new();
        public List<Action> Resetters = new();
    }

    /// <summary>
    /// Mirror of the old RenderForm: same order, same grouping, same row math —
    /// but prose goes to the canvas flow and controls become deferred specs.
    /// Returns the block height; control rows are blank placeholders.
    /// </summary>
    internal int FlowForm(Flow f, FormData form, int x, int y, int w)
    {
        int start = f.Lines.Count;
        int formIndex = f.NewFormIndex();
        if (!string.IsNullOrWhiteSpace(form.Title))
            f.AddText(TextUtil.Wrap(form.Title, w), w);

        var radioGroups = new Dictionary<string, List<FormControl>>();
        foreach (var c in form.Controls)
        {
            if (c.Kind != "radio" || c.Disabled) continue;
            if (!radioGroups.TryGetValue(c.Name, out var g))
            {
                g = new List<FormControl>();
                radioGroups[c.Name] = g;
            }
            g.Add(c);
        }
        var emittedRadios = new HashSet<string>();

        var buttons = new List<(FormControl Ctrl, bool IsReset)>();
        foreach (var c in form.Controls)
        {
            if (c.Kind == "hidden")
            {
                f.Specs.Add(new HiddenSpec(form, formIndex, c));
                continue;
            }
            if (c.Kind == "radio")
            {
                if (!emittedRadios.Add(c.Name)) continue;
                if (!radioGroups.TryGetValue(c.Name, out var group)) continue; // disabled-only group
                var distinct = group.Select(g => g.Label)
                    .Where(l => l.Length > 0 && l != "radio").Distinct().ToList();
                string prompt = distinct.Count == 1 ? distinct[0] : "";
                if (prompt.Length > 0)
                    f.AddText(TextUtil.Wrap(TextConcat.WithColon(prompt), w), w);
                int sh = Math.Max(1, group.Select(g => g.Label.Length > 0 ? g.Label : g.Value).Count());
                f.Specs.Add(new OptionSpec(form, formIndex, group, null, prompt,
                    group.Select(g => g.Label.Length > 0 ? g.Label : g.Value).ToList(),
                    group.FindIndex(g => g.Checked), c.Disabled, x, f.Lines.Count, w));
                f.AddBlank(sh);
                f.AddBlank(1);
                continue;
            }
            if (c.Kind == "select")
            {
                int sel = c.Options.FindIndex(o => o.Selected);
                if (c.Label.Length > 0 && c.Label != "choose")
                    f.AddText(TextUtil.Wrap(TextConcat.WithColon(c.Label), w), w);
                int sh = Math.Max(1, c.Options.Count);
                f.Specs.Add(new OptionSpec(form, formIndex, null, c, c.Label,
                    c.Options.Select(o => o.Text).ToList(), sel, c.Disabled, x, f.Lines.Count, w));
                f.AddBlank(sh);
                f.AddBlank(1);
                continue;
            }
            if (c.Kind is "submit" or "reset" or "button" or "image")
            {
                buttons.Add((c, c.Kind == "reset"));
                continue;
            }
            if (c.Label.Length > 0)
                f.AddText(TextUtil.Wrap(TextConcat.WithColon(c.Label), w), w);
            int ch = c.Kind == "textarea" ? 4 : 1;
            f.Specs.Add(new FieldSpec(form, formIndex, c, x, f.Lines.Count, w));
            f.AddBlank(ch);
            f.AddBlank(1);
        }

        int bx = x;
        foreach (var (ctrl, isReset) in buttons)
        {
            int bw = Math.Min(ctrl.Label.Length + 6, w);
            if (bx > x && bx + bw > x + w) { bx = x; f.AddBlank(1); }
            f.Specs.Add(new ButtonSpec(form, formIndex, ctrl, isReset, bx, f.Lines.Count, bw));
            bx += bw + 1;
        }
        if (buttons.Count > 0) f.AddBlank(1);
        return f.Lines.Count - start;
    }

    private void BuildControls(View parent, Flow f)
    {
        foreach (var group in f.Specs.GroupBy(s => s.FormIndex).OrderBy(g => g.Key))
        {
            var first = group.First();
            var bound = new BoundForm { Spec = first.Form };
            foreach (var spec in group)
            {
                switch (spec)
                {
                    case HiddenSpec h:
                    {
                        var sc = h.Ctrl;
                        bound.Getters.Add(() => new ControlState(sc, sc.Value, false, 0));
                        break;
                    }
                    case FieldSpec fd:
                        BuildField(parent, fd, bound);
                        break;
                    case OptionSpec os:
                        BuildOption(parent, os, bound);
                        break;
                    case ButtonSpec bs:
                        BuildButton(parent, bs, bound);
                        break;
                }
            }
        }
    }

    private void BuildField(View parent, FieldSpec s, BoundForm bound)
    {
        var c = s.Ctrl;
        int x = s.X, y = s.Y, w = s.W;
        switch (c.Kind)
        {
            case "textarea":
            {
                var tv = new TextView
                {
                    X = x, Y = y, Width = w, Height = 4,
                    Text = c.Value, ReadOnly = false, WordWrap = true, Multiline = true,
                    TabKeyAddsTab = false,
                    EnterKeyAddsLine = true,
                };
                if (c.Disabled) tv.Enabled = false;
                parent.Add(tv);
                bound.Getters.Add(() => new ControlState(c, tv.Text ?? "", false, 0));
                bound.Resetters.Add(() => tv.Text = c.Value);
                break;
            }
            case "checkbox":
            {
                var cb = new CheckBox
                {
                    X = x, Y = y, Width = w, Height = 1,
                    Text = c.Label.Length > 0 && c.Label != "checkbox" ? c.Label : c.Name,
                    Value = c.Checked ? CheckState.Checked : CheckState.UnChecked,
                };
                if (c.Disabled) cb.Enabled = false;
                AttachInputContextMenu(cb);
                parent.Add(cb);
                bound.Getters.Add(() => new ControlState(c, "", cb.Value == CheckState.Checked, 0));
                bound.Resetters.Add(() => cb.Value = c.Checked ? CheckState.Checked : CheckState.UnChecked);
                break;
            }
            case "file":
            {
                var tf = new TextField { X = x, Y = y, Width = w, Height = 1, Text = c.Value };
                if (c.Disabled) tf.Enabled = false;
                parent.Add(tf);
                bound.Getters.Add(() => new ControlState(c, tf.Text ?? "", false, 0));
                bound.Resetters.Add(() => tf.Text = c.Value);
                break;
            }
            default:
            {
                var tf = new TextField { X = x, Y = y, Width = w, Height = 1, Text = c.Value };
                if (c.Kind == "password") tf.Secret = true;
                if (c.Disabled) tf.Enabled = false;
                else if (SubmitsOnEnter(bound.Spec))
                {
                    tf.Accepting += (_, e) => { Submit(bound, "", ""); e.Handled = true; };
                    tf.KeyDown += (_, k) =>
                    {
                        try
                        {
                            if (k is not null && k == Key.Enter)
                            {
                                Submit(bound, "", "");
                                k.Handled = true;
                            }
                        }
                        catch { }
                    };
                }
                parent.Add(tf);
                bound.Getters.Add(() => new ControlState(c, tf.Text ?? "", false, 0));
                bound.Resetters.Add(() => tf.Text = c.Value);
                break;
            }
        }
    }

    private void BuildOption(View parent, OptionSpec s, BoundForm bound)
    {
        var shown = s.Options.Select(t => t.Length > 0 ? t : "(empty)").ToList();
        var sel = new OptionSelector
        {
            X = s.X, Y = s.Y, Width = s.W, Height = Math.Max(1, shown.Count),
            Labels = shown,
            Value = s.Selected >= 0 ? s.Selected : null,
            Orientation = Orientation.Vertical,
        };
        if (s.Disabled) sel.Enabled = false;
        AttachInputContextMenu(sel);
        parent.Add(sel);
        if (s.Group != null)
        {
            var opts = s.Group;
            for (int i = 0; i < opts.Count; i++)
            {
                int idx = i;
                var spec = opts[idx];
                bound.Getters.Add(() => new ControlState(spec, "", sel.Value == idx, 0));
            }
            int initSel = s.Selected;
            bound.Resetters.Add(() => sel.Value = initSel >= 0 ? initSel : null);
        }
        else if (s.Select is { } spec)
        {
            bound.Getters.Add(() => new ControlState(spec, "", false, sel.Value ?? 0));
            int initSel = s.Selected;
            bound.Resetters.Add(() => sel.Value = initSel >= 0 ? initSel : null);
        }
    }

    private void BuildButton(View parent, ButtonSpec s, BoundForm bound)
    {
        var ctrl = s.Ctrl;
        // Same single-row shadow trap as the nav chrome: Button's default
        // Opaque shadow steals the only content row (Viewport.Height=0), so
        // form submits/resets would also render blank. Null keeps Height=1 visible.
        var btn = new Button { X = s.X, Y = s.Y, Width = s.W, Height = 1, Text = ctrl.Label, ShadowStyle = null };
        if (ctrl.Disabled) btn.Enabled = false;
        else if (s.IsReset) btn.Accepting += (_, e) => { foreach (var r in bound.Resetters) r(); e.Handled = true; };
        else if (ctrl.Kind == "button") btn.Accepting += (_, e) =>
        {
            _actions.Notify($"“{ctrl.Label}” needs page scripting, which a text browser cannot run.");
            e.Handled = true;
        };
        else btn.Accepting += (_, e) => { Submit(bound, ctrl.Name, ctrl.Value); e.Handled = true; };
        AttachInputContextMenu(btn);
        parent.Add(btn);
    }

    private static bool SubmitsOnEnter(FormData form) =>
        form.Controls.Any(c => c.Kind is "submit" or "image" && !c.Disabled);

    private void AttachInputContextMenu(View v)
    {
        v.MouseEvent += (_, m) =>
        {
            if (m is null) return;
            if (!m.Flags.HasFlag(MouseFlags.RightButtonClicked)) return;
            ShowContextMenu(v, m);
            m.Handled = true;
        };
    }

    // ---------------- link / context helpers (kept API) ----------------

    /// <summary>Register a popover with the running app (no-op headless).</summary>
    private void RegisterMenu(PopoverMenu menu)
    {
        try { App?.Popovers?.Register(menu); }
        catch { /* showing still attempted; MakeVisible failure notifies */ }
    }

    /// <summary>Test helper: navigate as a link click would (left-only).</summary>
    internal bool TryNavigateLink(int index)
    {
        if (index < 1 || index > _links.Count) return false;
        _actions.Navigate(_links[index - 1].Url);
        return true;
    }

    internal void ShowLinkContextMenu(string url, Mouse m)
    {
        try
        {
            var items = new List<MenuItem>
            {
                new MenuItem("_Copy link URL", "", () => _actions.CopyToClipboard(url)),
                new MenuItem("_Open link", "", () => _actions.Navigate(url)),
                new MenuItem("Take _screenshot", "", () => _actions.TakeScreenshot()),
            };
            var menu = new PopoverMenu(items);
            RegisterMenu(menu);
            try
            {
                try { menu.MakeVisible(m.ScreenPosition); }
                catch { menu.MakeVisible(); }
            }
            catch { _actions.Notify("menu unavailable"); }
        }
        catch (Exception ex) { _actions.Notify($"menu failed: {ex.Message}"); }
    }

    internal void ShowContextMenu(View? target, Mouse m)
    {
        try
        {
            string? linkUrl = null;
            string? selected = null;
            if (target is TextView tv)
            {
                try { selected = tv.SelectedText; } catch { selected = null; }
                if (string.IsNullOrEmpty(selected) && m.Position is { } p && tv is PageTextView pc)
                {
                    linkUrl = pc.HitTestUrl(p.X, p.Y);
                }
            }
            else if (target is TextField tf)
            {
                try { selected = tf.SelectedText; } catch { selected = null; }
            }

            var items = new List<MenuItem>();
            if (!string.IsNullOrEmpty(selected))
            {
                string sel = selected;
                items.Add(new MenuItem("_Copy selection", "", () => _actions.CopyToClipboard(sel)));
            }
            else if (!string.IsNullOrEmpty(linkUrl))
            {
                string url = linkUrl;
                items.Add(new MenuItem("_Copy link URL", "", () => _actions.CopyToClipboard(url)));
            }
            else
            {
                items.Add(new MenuItem("_Copy", "", () =>
                {
                    try
                    {
                        if (target is TextView t2 && t2.SelectedLength > 0) { t2.Copy(); return; }
                        if (target is TextField f2 && f2.SelectedLength > 0) { f2.Copy(); return; }
                    }
                    catch { }
                    _actions.Notify("nothing selected to copy");
                }));
            }

            items.Add(new MenuItem("Select _all", "", () =>
            {
                try
                {
                    if (target is TextView t2) { t2.SelectAll(); return; }
                    if (target is TextField f2) { f2.SelectAll(); return; }
                }
                catch { }
                _actions.Notify("nothing to select");
            }));

            items.Add(new MenuItem("Take _screenshot", "", () => _actions.TakeScreenshot()));

            var menu = new PopoverMenu(items);
            RegisterMenu(menu);
            try
            {
                try { menu.MakeVisible(m.ScreenPosition); }
                catch { menu.MakeVisible(); }
            }
            catch { _actions.Notify("menu unavailable"); }
        }
        catch (Exception ex) { _actions.Notify($"menu failed: {ex.Message}"); }
    }

    private void Submit(BoundForm bound, string? submitterName, string? submitterValue)
    {
        var form = bound.Spec;
        bool httpBase = _baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        if (form.Action.Length == 0 && !httpBase)
        {
            _actions.Notify("This control is not inside a form, so there is nowhere to submit to.");
            return;
        }
        string action = form.Action.Length > 0
            ? (HtmlText.Resolve(form.Action, TryBase()) ?? _baseUrl)
            : _baseUrl;
        var states = bound.Getters.Select(g => g()).ToList();
        var (fields, files) = FormSubmit.Gather(states, submitterName, submitterValue);
        if (form.Method == "post")
        {
            bool multipart = form.Enctype == "multipart/form-data"
                           || states.Any(s => s.Control.Kind == "file" && s.Text.Length > 0
                                              && File.Exists(s.Text.Trim().Trim('"')));
            _actions.SubmitPost(action, fields, files, multipart);
        }
        else
        {
            _actions.Navigate(FormSubmit.GetUrl(action, fields));
        }
    }

    private Uri? TryBase()
    {
        try { return new Uri(_baseUrl); } catch { return null; }
    }
}
