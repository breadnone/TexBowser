using System.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TexBowser.Layout;

namespace TexBowser.Render;

/// <summary>One precomputed "[n]" marker span in canvas document coordinates.</summary>
public sealed record LinkSpan(int Line, int Start, int Length, string Url);

/// <summary>
/// The page canvas: a single read-only TextView holding the whole rendered
/// page as text. Native viewport scrolling draws only visible rows no matter
/// how long the page is, which keeps scrolling smooth where hundreds of
/// per-block subviews made it crawl. Form controls are added as children at
/// document coordinates and ride along with the viewport for free.
///
/// Mouse is handled here instead of per-link overlay views (one overlay per
/// link was the other view-count multiplier):
/// - wheel scrolls manually (no focus steal from the url box or inputs),
/// - hover only previews the URL (never focuses),
/// - plain left-click on a "[n]" marker navigates,
/// - everything else (press/drag/double-click/cursor keys) falls through to
///   the base TextView so native selection keeps working,
/// - right-click opens the copy/link/screenshot menu.
/// </summary>
public sealed class PageTextView : TextView
{
    private readonly IBrowserActions _actions;

    /// <summary>Canvas text, one entry per document line.</summary>
    public IReadOnlyList<string> DocLines { get; private set; } = Array.Empty<string>();

    /// <summary>Page links for index → URL resolution.</summary>
    public IReadOnlyList<LinkInfo> Links { get; private set; } = Array.Empty<LinkInfo>();

    /// <summary>Precomputed marker spans (built once per page, not per frame).</summary>
    public IReadOnlyList<LinkSpan> Spans { get; private set; } = Array.Empty<LinkSpan>();

    /// <summary>Right-click menu hooks (wired by the renderer; null = native only).</summary>
    public Action<string, Mouse>? LinkMenu { get; set; }

    /// <summary>Right-click menu hooks (wired by the renderer; null = native only).</summary>
    public Action<TextView, Mouse>? PageMenu { get; set; }

    public PageTextView(IBrowserActions actions)
    {
        _actions = actions;
        ReadOnly = true;
        WordWrap = false; // lines arrive pre-wrapped to the viewport width
        Multiline = true;
        CanFocus = true; // keyboard selection + copy need focus
        TabStop = TabBehavior.NoStop; // Tab stays on chrome + form inputs
        BorderStyle = Terminal.Gui.Drawing.LineStyle.None;
        MousePositionTracking = true; // hover previews
    }

    /// <summary>Load new document text + links; resets scroll to top.</summary>
    public void SetDocument(List<string> lines, IReadOnlyList<LinkInfo> links)
    {
        DocLines = lines;
        Links = links;
        Text = TextConcat.JoinLines(lines);
        var spans = new List<LinkSpan>();
        for (int i = 0; i < lines.Count; i++)
        {
            foreach (var (index, start, length) in LinkHitTest.FindAllSpans(lines[i]))
            {
                if (index >= 1 && index <= links.Count)
                    spans.Add(new LinkSpan(i, start, length, links[index - 1].Url));
            }
        }
        Spans = spans;
        try { Viewport = Viewport with { X = 0, Y = 0 }; }
        catch { /* headless (selftest): no layout yet */ }
    }

    /// <summary>URL under viewport-relative coordinates, or null.</summary>
    public string? HitTestUrl(int viewX, int viewY)
    {
        int sy, sx;
        try { sy = Viewport.Y; sx = Viewport.X; }
        catch { sy = 0; sx = 0; }
        int docLine = sy + viewY, docCol = sx + viewX;
        if (docLine < 0 || docLine >= DocLines.Count) return null;
        int? idx = LinkHitTest.FindLinkAt(DocLines[docLine], docCol);
        if (idx is not { } n || n < 1 || n > Links.Count) return null;
        return Links[n - 1].Url;
    }

    /// <summary>
    /// Wheel step for one notch: a quarter viewport (snappy but still
    /// precise), clamped to 3..15 lines. Pure for headless testing.
    /// Viewport 0 (headless/pre-layout) falls back to 5.
    /// </summary>
    internal static int WheelStep(int viewportHeight) =>
        viewportHeight <= 0 ? 5 : Math.Clamp(viewportHeight / 4, 3, 15);

    /// <summary>Current vertical scroll offset (0 when unavailable).</summary>
    public int ScrollY
    {
        get { try { return Viewport.Y; } catch { return 0; } }
    }

    private int CurrentWheelStep()
    {
        try { return WheelStep(Viewport.Height); }
        catch { return 5; }
    }

    protected override bool OnMouseEvent(Mouse mouse)
    {
        // Wheel: scroll manually so focus never jumps out of the url box
        // or a form field just because the pointer happened to be over text.
        // Step is viewport-proportional (quarter screen): the old fixed 3-line
        // step needed a flood of wheel events for any real distance, and each
        // event paid a full redraw, which is what felt "late". One notch now
        // moves a visible chunk with a single redraw.
        if (mouse.IsWheel)
        {
            try
            {
                int step = CurrentWheelStep();
                if (mouse.Flags.HasFlag(MouseFlags.WheeledUp)) ScrollVertical(-step);
                else if (mouse.Flags.HasFlag(MouseFlags.WheeledDown)) ScrollVertical(step);
                else return base.OnMouseEvent(mouse);
            }
            catch { return base.OnMouseEvent(mouse); }
            mouse.Handled = true;
            return true;
        }

        bool activity = mouse.IsPressed || mouse.IsReleased || mouse.IsSingleDoubleOrTripleClicked;
        if (!activity)
        {
            // Hover (position report, no buttons): preview only, never focus.
            string? url = null;
            if (mouse.Position is { } hp) url = HitTestUrl(hp.X, hp.Y);
            _actions.PreviewLink(url);
            mouse.Handled = true;
            return true;
        }

        if (mouse.Flags.HasFlag(MouseFlags.RightButtonClicked))
        {
            if (mouse.Position is { } rp && HitTestUrl(rp.X, rp.Y) is { } url)
                LinkMenu?.Invoke(url, mouse);
            else
                PageMenu?.Invoke(this, mouse);
            // Fall back to the native context menu when nobody handles it.
            if (!mouse.Handled) return base.OnMouseEvent(mouse);
            return true;
        }

        if (mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked)
            && !mouse.Flags.HasFlag(MouseFlags.RightButtonClicked)
            && !mouse.Flags.HasFlag(MouseFlags.MiddleButtonClicked)
            && (mouse.Flags & (MouseFlags.Ctrl | MouseFlags.Shift | MouseFlags.Alt)) == 0)
        {
            // Click with an active selection clears it instead of navigating.
            try { if (SelectedLength > 0) return base.OnMouseEvent(mouse); }
            catch { /* headless: no selection state */ }
            if (mouse.Position is { } cp && HitTestUrl(cp.X, cp.Y) is { } target)
            {
                _actions.Navigate(target);
                mouse.Handled = true;
                return true;
            }
            return base.OnMouseEvent(mouse);
        }

        return base.OnMouseEvent(mouse);
    }
}
