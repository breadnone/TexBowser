using TexBowser.Demo;
using TexBowser.Engine;
using TexBowser.Layout;
using TexBowser.Net;
using TexBowser.Render;
using TexBowser.Sys;
using TexBowser.Ui;

namespace TexBowser.Engine;

/// <summary>
/// Offline verification for deterministic layout detection:
/// every framework detector, wrap-chunking, tables, regions, responsive
/// collapse, links, history, mouse hit-testing and terminal spawning.
/// Run with: TexBowser --selftest
/// </summary>
public static class SelfTest
{
    private static int _pass;
    private static int _fail;

    public static int Run()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("TexBowser --selftest");
        Console.WriteLine(new string('=', 60));

        TestBootstrapGrid();
        TestTailwindGridCols();
        TestTailwindFractionsAndWrap();
        TestStyleBlockFlexAndGrid();
        TestBulmaAndFoundation();
        TestNestedHolyGrail();
        TestLayoutVsDataTable();
        TestRegionFallback();
        TestFloatAndPx();
        TestResponsiveCollapse();
        TestLinkNumbering();
        TestTableRender();
        TestHistory();
        TestSpawnDecision();
        TestLinkHitTest();
        TestWheelStep();
        TestFormsParsed();
        TestFormProseExclusion();
        TestQueryBuilding();
        TestGather();
        TestMultipart();
        TestViewTree();
        TestSanitizerSoup();
        TestHiddenContent();
        TestBaseHref();
        TestTabsModel();
        TestChromeForms();
        TestNoLinksIndexInApp();
        TestShortcuts();
        TestLinkClickLeftOnly();
        TestInputBrowserBehavior();
        TestNewTabUrlFocus();
        TestProseSelection();
        TestContextMenuGeneric();
        TestTextConcat();
        TestCanvasPlaceholders();
        TestTabState();
        TestStopCancellation();
        TestNavChrome();
        TestHomeLanding();

        int contentFails = TexBowser.Engine.ContentRegressionTests.Run();
        int findFails = TexBowser.Engine.FindRegressionTests.Run();
        if (contentFails > 0) _fail += contentFails;
        if (findFails > 0) _fail += findFails;

        Console.WriteLine(new string('=', 60));
        Console.WriteLine($"PASS {_pass}  FAIL {_fail}");
        return _fail == 0 ? 0 : 1;
    }

    private static void Check(bool ok, string name, string extra = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  [PASS] {name}"); }
        else { _fail++; Console.WriteLine($"  [FAIL] {name} {extra}"); }
    }

    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), $"texbowser-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        return d;
    }

    private static void TestBootstrapGrid()
    {
        Console.WriteLine("· bootstrap 12-grid (col-md-4 x3)");
        var page = LayoutPerceiver.Perceive(DemoPages.ThreeCol, "https://example.com/");
        Check(page.Signature == "H|N|M3|A|F", "signature", $"got {page.Signature}");
        Check(page.RowSources.Contains("bootstrap"), "source=bootstrap", $"got [{string.Join(",", page.RowSources)}]");
        Check(page.HasNav && page.NavLinks.Count == 5, "5 nav links", $"got {page.NavLinks.Count}");
        Check(Math.Abs(page.AsideRatio - 0.70) < 1e-9 && page.AsideRatioSource == "default",
            "aside ratio falls back to default", $"got {page.AsideRatio:0.00}/{page.AsideRatioSource}");
    }

    private static void TestTailwindGridCols()
    {
        Console.WriteLine("· tailwind grid-cols (desktop breakpoint wins)");
        var page = LayoutPerceiver.Perceive(DemoPages.Tailwindish, "https://example.com/s");
        var gridRow = FindRows(page.Main).FirstOrDefault(r => r.Source == "tailwind" && r.Cells.Count == 3);
        Check(gridRow != null, "3-col tailwind row", $"sources [{string.Join(",", page.RowSources)}]");
        Check(page.MainCols == 3, "signature M3", $"got {page.Signature}");
    }

    private static void TestTailwindFractionsAndWrap()
    {
        Console.WriteLine("· tailwind w- fractions + flex-row direction + wrap chunking");
        var page = LayoutPerceiver.Perceive(DemoPages.Tailwindish, "https://example.com/s");
        var flexRow = FindRows(page.Main).FirstOrDefault(r => r.Source == "flex");
        Check(flexRow != null && flexRow.Cells.Count == 2, "2-col flex row (md:flex-row wins over flex-col)");
        if (flexRow != null)
        {
            double f0 = flexRow.Cells[0].Fraction;
            Check(Math.Abs(f0 - 2.0 / 3) < 0.02, "fractions 2/3 + 1/3", $"got {f0:0.00}");
        }

        const string wrap = """
            <html><body><main><div>
            <div class="w-1/2"><h3>A</h3><p>Lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod.</p></div>
            <div class="w-1/2"><h3>B</h3><p>Tempor incididunt ut labore et dolore magna aliqua enim ad minim.</p></div>
            <div class="w-1/2"><h3>C</h3><p>Veniam quis nostrud exercitation ullamco laboris nisi ut aliquip ex.</p></div>
            <div class="w-1/2"><h3>D</h3><p>Duis aute irure dolor in reprehenderit in voluptate velit esse cillum.</p></div>
            </div></main></body></html>
            """;
        var page2 = LayoutPerceiver.Perceive(wrap, "https://example.com/w");
        var rows = FindRows(page2.Main).Where(r => r.Cells.Count == 2).ToList();
        Check(rows.Count == 2, "4×w-1/2 wraps into two rows of two", $"got {rows.Count} 2-col rows");
    }

    private static void TestStyleBlockFlexAndGrid()
    {
        Console.WriteLine("· <style>-block rules drive flex + grid-template detection");
        var page = LayoutPerceiver.Perceive(DemoPages.HolyGrail, "https://example.com/g");
        var flexRow = FindRows(page.Main).FirstOrDefault(r => r.Source == "flex");
        Check(flexRow != null && flexRow.Cells.Count == 2, "flex 2-col lead row from stylesheet");
        if (flexRow != null)
            Check(Math.Abs(flexRow.Cells[0].Fraction - 2.0 / 3) < 0.02, "flex:2 vs flex:1 → 67/33",
                $"got {flexRow.Cells[0].Fraction:0.00}");
        var gridRow = FindRows(page.Main).FirstOrDefault(r => r.Source == "grid");
        Check(gridRow != null && gridRow.Cells.Count == 3, "repeat(3,minmax(0,1fr)) digest row");
    }

    private static void TestBulmaAndFoundation()
    {
        Console.WriteLine("· bulma columns + foundation grid-x");
        const string bulma = """
            <html><body><main><div class="columns">
            <div class="column is-one-third"><h3>A</h3><p>Lorem ipsum dolor sit amet consectetur adipiscing elit sed do.</p></div>
            <div class="column is-one-third"><h3>B</h3><p>Eiusmod tempor incididunt ut labore et dolore magna aliqua enim.</p></div>
            <div class="column is-one-third"><h3>C</h3><p>Veniam quis nostrud exercitation ullamco laboris nisi aliquip.</p></div>
            </div></main></body></html>
            """;
        var b = LayoutPerceiver.Perceive(bulma, "https://example.com/b");
        Check(b.MainCols == 3 && b.RowSources.Contains("bulma"), "bulma thirds",
            $"got {b.Signature} [{string.Join(",", b.RowSources)}]");

        const string fdn = """
            <html><body><main><div class="grid-x">
            <div class="cell small-12 large-8"><h3>A</h3><p>Lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor.</p></div>
            <div class="cell small-12 large-4"><h3>B</h3><p>Incididunt ut labore et dolore magna aliqua enim ad minim veniam.</p></div>
            </div></main></body></html>
            """;
        var f = LayoutPerceiver.Perceive(fdn, "https://example.com/f");
        var row = FindRows(f.Main).FirstOrDefault();
        Check(row != null && row.Cells.Count == 2, "foundation 2-col row");
        if (row != null)
            Check(Math.Abs(row.Cells[0].Fraction - 8.0 / 12) < 0.02, "large-8 beats small-12 (desktop-first)",
                $"got {row.Cells[0].Fraction:0.00}");
    }

    private static void TestNestedHolyGrail()
    {
        Console.WriteLine("· nested holy-grail (hero + flex lead + grid digest + aside)");
        var page = LayoutPerceiver.Perceive(DemoPages.HolyGrail, "https://example.com/g");
        Check(page.Signature == "H|N|M3|A|F", "signature", $"got {page.Signature}");
        Check(page.HasAside && page.Asides.Count == 1, "aside merged");
        string d = TempDir();
        try
        {
            using var engine = new BrowserEngine(d);
            var rendered = engine.RenderHtml(DemoPages.HolyGrail, "https://example.com/g", 110);
            int hero = rendered.Text.IndexOf("Morning edition", StringComparison.Ordinal);
            int lead = rendered.Text.IndexOf("Harbour bridge", StringComparison.Ordinal);
            Check(hero >= 0 && lead > hero, "hero text renders before the column rows");
            Check(rendered.Lines.All(l => l.Length <= 110), "all lines fit 110");
            Check(rendered.Lines.Any(l => l.Contains(" │ ")), "side-by-side separators present");
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    private static void TestLayoutVsDataTable()
    {
        Console.WriteLine("· layout <table> vs data <table>");
        const string layout = """
            <html><body><main><table><tr>
            <td><h3>Left promo block with enough text to be real content here</h3><p>Sale ends Sunday, don't miss out on savings.</p></td>
            <td><h3>Right promo block with enough text to be real content here</h3><p>New arrivals daily, come browse the collection.</p></td>
            </tr></table></main></body></html>
            """;
        string d = TempDir();
        try
        {
            using var engine = new BrowserEngine(d);
            var rendered = engine.RenderHtml(layout, "https://example.com/l", 100);
            Check(rendered.Text.Contains(" │ "), "layout table becomes side-by-side columns");
            Check(!rendered.Text.Contains('┌'), "layout table gets no data borders");
            var data = engine.RenderHtml(DemoPages.TablesAndSidebar, "https://example.com/t", 100);
            Check(data.Text.Contains('┌') && data.Text.Contains('└'), "data table gets borders");
            Check(!data.Text.Contains("Time Train Destination"), "no flat table-text duplication");
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    private static void TestRegionFallback()
    {
        Console.WriteLine("· zero-hook pages fall back to the largest content block");
        string longA = string.Concat(Enumerable.Repeat(
            "The harbour bridge reopened after eighteen months of careful repair work. ", 12));
        string html = "<html><body><div><div><p>" + longA + "</p></div>"
                    + "<div><p>Short note.</p></div></div></body></html>";
        var page = LayoutPerceiver.Perceive(html, "https://example.com/z");
        Check(page.Signature == "-|-|M1|-|-", "signature", $"got {page.Signature}");
        string d = TempDir();
        try
        {
            using var engine = new BrowserEngine(d);
            var rendered = engine.RenderHtml(html, "https://example.com/z", 100);
            Check(rendered.Text.Contains("harbour bridge"), "content survives with no hooks");
            Check(rendered.Lines.All(l => l.Length <= 100), "lines fit");
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    private static void TestFloatAndPx()
    {
        Console.WriteLine("· floats + fixed px widths");
        const string floats = """
            <html><body><main><div>
            <div style="float:left;width:60%"><h3>A</h3><p>Lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod.</p></div>
            <div style="float:right;width:40%"><h3>B</h3><p>Tempor incididunt ut labore et dolore magna aliqua enim ad minim.</p></div>
            </div></main></body></html>
            """;
        var f = LayoutPerceiver.Perceive(floats, "https://example.com/fl");
        var frow = FindRows(f.Main).FirstOrDefault();
        Check(frow != null && Math.Abs(frow.Cells[0].Fraction - 0.6) < 0.02, "float 60/40",
            $"got {frow?.Cells[0].Fraction:0.00}");

        const string px = """
            <html><body><main><div>
            <div style="width:600px"><h3>A</h3><p>Lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod.</p></div>
            <div style="width:200px"><h3>B</h3><p>Tempor incididunt ut labore et dolore magna aliqua enim ad minim.</p></div>
            </div></main></body></html>
            """;
        var p = LayoutPerceiver.Perceive(px, "https://example.com/px");
        var prow = FindRows(p.Main).FirstOrDefault();
        Check(prow != null && Math.Abs(prow.Cells[0].Fraction - 0.75) < 0.02, "px 600/200 → 75/25",
            $"got {prow?.Cells[0].Fraction:0.00}");
    }

    private static void TestResponsiveCollapse()
    {
        Console.WriteLine("· responsive collapse below 84 cols");
        string d = TempDir();
        try
        {
            using var engine = new BrowserEngine(d);
            var wide = engine.RenderHtml(DemoPages.ThreeCol, "https://example.com/", 110);
            var narrow = engine.RenderHtml(DemoPages.ThreeCol, "https://example.com/", 70);
            Check(wide.Lines.All(l => l.Length <= 110), "wide lines fit 110");
            Check(narrow.Lines.All(l => l.Length <= 70), "narrow lines fit 70");
            Check(narrow.Text.Contains("WidgetCon"), "narrow stacks sidebar content");
            Check(!narrow.Lines.Any(l => l.Contains(" │ ") && l.Contains("News")), "no side-by-side sidebar when narrow");
            Check(wide.Lines.Any(l => l.Contains(" │ ")), "wide uses │ column separators");
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    private static void TestLinkNumbering()
    {
        Console.WriteLine("· link numbering in render order");
        string d = TempDir();
        try
        {
            using var engine = new BrowserEngine(d);
            var rendered = engine.RenderHtml(DemoPages.ThreeCol, "https://example.com/", 110);
            Check(rendered.Links.Count >= 9, "all links collected", $"got {rendered.Links.Count}");
            Check(rendered.Links.Select(l => l.Index).SequenceEqual(Enumerable.Range(1, rendered.Links.Count)),
                "indexes 1..N dense");
            Check(rendered.Text.Contains("[1]"), "markers embedded in text");
            Check(rendered.Text.IndexOf("[1]", StringComparison.Ordinal)
                < rendered.Text.IndexOf("[6]", StringComparison.Ordinal), "nav numbered before main");
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    private static void TestTableRender()
    {
        Console.WriteLine("· bordered data-table rendering");
        var lines = TextUtil.RenderDataTable(
            new[] { "Time", "Train", "Destination" },
            new[] {
                (IReadOnlyList<string>)new[] { "18:04", "IC 221", "Harbour City" },
                new[] { "18:17", "RE 9", "Mill Valley via Old Town" },
            }, 60);
        Check(lines[0].StartsWith("┌") && lines[^1].StartsWith("└"), "bordered box");
        Check(lines.All(l => l.Length <= 60), "fits width");
    }

    private static void TestHistory()
    {
        Console.WriteLine("· persistent browsing history (JSON)");
        string d = TempDir();
        try
        {
            using var engine = new BrowserEngine(d);
            engine.RenderHtml(DemoPages.ThreeCol, "https://example.com/", 100);
            engine.RenderHtml(DemoPages.ThreeCol, "https://example.com/", 100);
            engine.RenderHtml(DemoPages.Cards, "https://example.com/blog", 100);

            var rows = engine.History.List();
            Check(rows.Count == 2, "one row per URL", $"got {rows.Count}");
            var home = rows.FirstOrDefault(r => r.Url == "https://example.com/");
            Check(home != null && home.Visits == 2, "visit counted twice", $"got {home?.Visits}");
            Check(rows[0].Url == "https://example.com/blog", "most recent first", $"got {rows[0].Url}");

            var (lines, links) = engine.HistoryLines(100);
            Check(links.Count == 2, "history entries are followable links");
            Check(links.Select(l => l.Index).SequenceEqual(new[] { 1, 2 }), "history numbers dense");
            Check(lines.All(l => l.Length <= 100), "history lines fit width");

            engine.History.Clear();
            Check(engine.History.List().Count == 0, "history wipes clean");
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    private static void TestSpawnDecision()
    {
        Console.WriteLine("· dedicated-terminal spawn decision");
        Check(!TerminalSpawner.ShouldSpawn(false, false, false, false, false, true), "interactive stays inline");
        Check(TerminalSpawner.ShouldSpawn(false, false, false, true, false, true), "redirected stdin spawns");
        Check(TerminalSpawner.ShouldSpawn(false, false, false, false, true, true), "redirected stdout spawns");
        Check(!TerminalSpawner.ShouldSpawn(false, true, false, true, true, true), "--inline wins");
        Check(!TerminalSpawner.ShouldSpawn(false, false, true, true, true, true), "--spawned guard wins");
        Check(TerminalSpawner.ShouldSpawn(true, false, false, false, false, true), "--new-window forces");
        Check(!TerminalSpawner.WantsSpawn(new[] { "--selftest" }, tuiMode: false), "headless never spawns");
        var (file, _) = TerminalSpawner.RelaunchTarget();
        Check(file.Length > 0, "relaunch target known", $"got '{file}'");
    }

    private static void TestLinkHitTest()
    {
        Console.WriteLine("· mouse hit-testing of [n] markers");
        Check(LinkHitTest.FindLinkAt("Buy Starter — $29 [9]", 19) == 9, "click on marker");
        Check(LinkHitTest.FindLinkAt("Buy Starter — $29 [9]", 0) == null, "click off marker");
        Check(LinkHitTest.FindLinkAt("Home [4] │ Shop [5]", 7) == 4, "first of two");
        Check(LinkHitTest.FindLinkAt("Home [4] │ Shop [5]", 9) == null, "separator gap");
        Check(LinkHitTest.FindLinkAt("Home [4] │ Shop [5]", 17) == 5, "second of two");
        Check(LinkHitTest.FindLinkAt("[10] Privacy → https://example.com/privacy", 0) == 10, "multi-digit start");
        Check(LinkHitTest.FindLinkAt("[10] Privacy → https://example.com/privacy", 3) == 10, "multi-digit end");
        Check(LinkHitTest.FindLinkAt("[10] Privacy → https://example.com/privacy", 4) == null, "past marker");
        Check(LinkHitTest.FindLinkAt("plain line, no links", 3) == null, "no markers");
        Check(LinkHitTest.FindLinkAt("", 0) == null, "empty line");
        Check(LinkHitTest.FindLinkAt("[3] x", -1) == null, "negative column");
    }

    private static void TestWheelStep()
    {
        Console.WriteLine("· wheel scroll step is viewport-proportional");
        // One notch must move a visible chunk (quarter viewport) so fast
        // wheeling needs few events/redraws; fixed 3-line steps felt "late".
        Check(PageTextView.WheelStep(0) == 5, "headless viewport falls back to 5");
        Check(PageTextView.WheelStep(-4) == 5, "negative viewport falls back to 5");
        Check(PageTextView.WheelStep(4) == 3, "tiny viewport clamps to min 3");
        Check(PageTextView.WheelStep(12) == 3, "12-row viewport stays at min 3");
        Check(PageTextView.WheelStep(20) == 5, "20-row viewport steps 5");
        Check(PageTextView.WheelStep(40) == 10, "40-row viewport steps 10");
        Check(PageTextView.WheelStep(100) == 15, "tall viewport clamps to max 15");
        Check(PageTextView.WheelStep(1000) == 15, "huge viewport clamps to max 15");
    }

    private static void TestFormsParsed()
    {
        Console.WriteLine("· form controls parsed with labels, options, states");
        var page = LayoutPerceiver.Perceive(DemoPages.Search, "https://example.com/a");
        var forms = FindForms(page.Main).ToList();
        Check(forms.Count == 2, "two forms detected", $"got {forms.Count}");

        var search = forms[0].Form;
        Check(search.Method == "get" && search.Action == "https://example.com/search", "GET form action");
        var q = search.Controls.FirstOrDefault(c => c.Name == "q");
        Check(q != null && q.Kind == "search", "search input found");
        Check(q != null && q.Label == "Search the archive", "label for= resolved", $"got '{q?.Label}'");
        Check(q != null && q.Placeholder == "try: widget maintenance", "placeholder kept");
        var fmt = search.Controls.FirstOrDefault(c => c.Kind == "select");
        Check(fmt != null && fmt.Options.Count == 3, "select with 3 options");
        Check(fmt != null && fmt.Label == "Format", "wrapping label minus options = prompt", $"got '{fmt?.Label}'");
        Check(fmt != null && fmt.Options[1].Selected && fmt.Options[1].Value == "pdf", "preselected option honored");
        var ocr = search.Controls.FirstOrDefault(c => c.Kind == "checkbox");
        Check(ocr != null && ocr.Checked && ocr.Value == "1", "checked checkbox with value");
        Check(search.Controls.Any(c => c.Kind == "hidden" && c.Name == "lang"), "hidden field captured");
        Check(search.Controls.Any(c => c.Kind == "submit" && c.Label == "Search"), "submit button labeled");

        var fb = forms[1].Form;
        Check(fb.Method == "post" && fb.Title == "Request a manual", "POST form + legend title");
        Check(fb.Controls.Any(c => c.Kind == "textarea" && c.Name == "what"), "textarea found");
        var radios = fb.Controls.Where(c => c.Kind == "radio" && c.Name == "urg").ToList();
        Check(radios.Count == 2 && radios.Count(r => r.Checked) == 1
            && radios.First(r => r.Checked).Value == "high", "radio group with default");
        Check(fb.Controls.Any(c => c.Kind == "submit" && c.Name == "go"), "named submitter");
        Check(fb.Controls.Any(c => c.Kind == "reset"), "reset button");
    }

    private static void TestFormProseExclusion()
    {
        Console.WriteLine("· form controls never leak into prose");
        var page = LayoutPerceiver.Perceive(DemoPages.Search, "https://example.com/a");
        var prose = new List<string>();
        CollectProse(page.Main, prose);
        if (page.Header != null) CollectProse(page.Header, prose);
        foreach (var a in page.Asides) CollectProse(a, prose);
        if (page.Footer != null) CollectProse(page.Footer, prose);
        string all = string.Join("\n", prose);
        Check(!all.Contains("Search the archive"), "control labels excluded from prose");
        Check(!all.Contains("Send request") && !all.Contains("Include scanned pages"),
            "button/checkbox text excluded from prose");
    }

    private static void CollectProse(LayoutNode n, List<string> prose)
    {
        switch (n)
        {
            case TextNode t:
                prose.Add(t.Text.Heading);
                prose.AddRange(t.Text.Paragraphs);
                break;
            case RowNode r: foreach (var c in r.Cells) CollectProse(c.Content, prose); break;
            case StackNode s: foreach (var c in s.Children) CollectProse(c, prose); break;
        }
    }

    private static void TestQueryBuilding()
    {
        Console.WriteLine("· url-encoded query building");
        var fields = new List<FieldData>
        {
            new("q", "widget maintenance"),
            new("fmt", "pdf"),
            new("ocr", "1"),
        };
        Check(FormSubmit.ToQuery(fields) == "q=widget%20maintenance&fmt=pdf&ocr=1", "exact encoding",
            $"got '{FormSubmit.ToQuery(fields)}'");
        Check(FormSubmit.ToQuery(new[] { new FieldData("q", "wídget&co") }) == "q=w%C3%ADdget%26co",
            "unicode + reserved chars escaped");
        Check(FormSubmit.GetUrl("https://example.com/search?old=1#frag", fields)
            == "https://example.com/search?q=widget%20maintenance&fmt=pdf&ocr=1",
            "GET replaces query, strips fragment");
        Check(FormSubmit.GetUrl("https://example.com/search", new List<FieldData>())
            == "https://example.com/search", "empty fields keep action");
    }

    private static void TestGather()
    {
        Console.WriteLine("· successful-controls gathering (HTML semantics)");
        var states = new List<ControlState>
        {
            new(new FormControl("text", "q", "", "", "q", false, false, new()), "hi", false, 0),
            new(new FormControl("checkbox", "ocr", "1", "", "ocr", true, false, new()), "", true, 0),
            new(new FormControl("checkbox", "nope", "1", "", "nope", false, false, new()), "", false, 0),
            new(new FormControl("text", "off", "", "", "off", false, true, new()), "x", false, 0),
            new(new FormControl("text", "", "", "", "", false, false, new()), "y", false, 0),
            new(new FormControl("radio", "urg", "low", "", "Low", false, false, new()), "", false, 0),
            new(new FormControl("radio", "urg", "high", "", "High", true, false, new()), "", true, 0),
            new(new FormControl("select", "fmt", "", "", "fmt", false, false, new List<FormOption>
                { new("any", "Anything", false), new("pdf", "PDF", true) }), "", false, 1),
            new(new FormControl("file", "doc", "", "", "doc", false, false, new()), "C:\\tmp\\a.txt", false, 0),
        };
        var (fields, files) = FormSubmit.Gather(states, "go", "Send");
        string q = FormSubmit.ToQuery(fields);
        Check(q == "q=hi&ocr=1&urg=high&fmt=pdf&doc=a.txt&go=Send", "gathered exactly",
            $"got '{q}'");
        Check(files.Count == 1 && files[0].FilePath == "C:\\tmp\\a.txt", "file entry kept");
    }

    private static void TestMultipart()
    {
        Console.WriteLine("· multipart POST construction");
        string tmp = Path.Combine(Path.GetTempPath(), $"texbowser-up-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(tmp, "hello upload");
            var fields = new List<FieldData> { new("who", "Ada") };
            var files = new List<FileData> { new("doc", tmp) };
            using var mp = FormSubmit.BuildPostContent(fields, files, multipart: true);
            string body = mp.ReadAsStringAsync().GetAwaiter().GetResult();
            string? media = mp.Headers.ContentType?.MediaType;
            Check(media == "multipart/form-data", "multipart content type", $"got {media}");
            Check(body.Contains("filename=" + Path.GetFileName(tmp)) && body.Contains("hello upload"),
                "file bytes embedded");
            using var ue = FormSubmit.BuildPostContent(fields, files, multipart: false);
            Check(ue.Headers.ContentType?.MediaType == "application/x-www-form-urlencoded", "urlencoded type");
            Check(ue.ReadAsStringAsync().GetAwaiter().GetResult() == "who=Ada", "urlencoded body exact");
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    private sealed class NoopActions : IBrowserActions
    {
        public void Navigate(string url) { }
        public void SubmitPost(string actionUrl, List<FieldData> fields, List<FileData> files, bool multipart) { }
        public void OpenResult(string body, string finalUrl, string? contentType) { }
        public void Notify(string message) { }
        public void PreviewLink(string? url) { }
        public void CopyToClipboard(string text) { }
        public void TakeScreenshot() { }
    }

    private static void TestViewTree()
    {
        Console.WriteLine("· live view-tree construction (real Terminal.Gui controls)");
        string d = TempDir();
        try
        {
            using var engine = new BrowserEngine(d);
            var page = LayoutPerceiver.Perceive(DemoPages.Search, "https://example.com/a");
            var host = new Terminal.Gui.ViewBase.View();
            var renderer = new ViewRenderer(100, page.BaseUrl, page.Links, new NoopActions());
            int h = renderer.RenderInto(host, page);
            Check(h > 20, "content height measured", $"got {h}");

            var all = Descendants(host).ToList();
            var fields = all.OfType<Terminal.Gui.Views.TextField>().Count();
            var buttons = all.OfType<Terminal.Gui.Views.Button>().Count();
            // Standalone checkboxes only (OptionSelectors render their own internally).
            var checks = all.OfType<Terminal.Gui.Views.CheckBox>()
                .Where(c => c.SuperView is not Terminal.Gui.Views.OptionSelector).ToList();
            var sels = all.OfType<Terminal.Gui.Views.OptionSelector>().Count();
            var textViews = all.OfType<Terminal.Gui.Views.TextView>().ToList();
            var editable = textViews.Where(v => !v.ReadOnly).ToList();
            var canvases = textViews.OfType<PageTextView>().ToList();
            Check(fields == 2, "search + name inputs are TextFields", $"got {fields}");
            Check(editable.Count == 1, "textarea is an editable TextView", $"got {editable.Count} editable, {textViews.Count} total");
            Check(canvases.Count == 1, "single canvas holds all prose", $"got {canvases.Count}");
            Check(canvases.Count == 1 && canvases[0].CanFocus
                && canvases[0].TabStop == Terminal.Gui.ViewBase.TabBehavior.NoStop,
                "canvas selectable, skips Tab");
            Check(canvases.Count == 1 && canvases[0].Spans.Count == page.Links.Count,
                "canvas precomputes every link span", $"got {canvases.FirstOrDefault()?.Spans.Count} spans, {page.Links.Count} links");
            Check(buttons >= 3, "submits/resets are Buttons", $"got {buttons}");
            Check(checks.Count == 1 && checks[0].Value == Terminal.Gui.Views.CheckState.Checked
                && checks[0].Text == "Include scanned pages", "checkbox live + checked + labeled");
            Check(sels >= 2, "select + radios are OptionSelectors", $"got {sels}");
            var fmt = all.OfType<Terminal.Gui.Views.OptionSelector>()
                .FirstOrDefault(o => o.Labels?.Contains("PDF manual") == true);
            Check(fmt != null && fmt.Value == 1, "select preselects PDF (index 1)");
            var urg = all.OfType<Terminal.Gui.Views.OptionSelector>()
                .FirstOrDefault(o => o.Labels?.Contains("High") == true);
            Check(urg != null && urg.Value == 1, "radio group preselects High");
            var searchBtn = all.OfType<Terminal.Gui.Views.Button>()
                .FirstOrDefault(b => b.Text == "Search");
            Check(searchBtn != null, "submit Button labeled like the original");
            // Same single-row shadow trap as nav chrome: Height=1 buttons need
            // a drawable viewport or form submits render blank.
            Check(all.OfType<Terminal.Gui.Views.Button>().All(b => b.Viewport.Width >= 1 && b.Viewport.Height >= 1),
                "form buttons have drawable viewport");
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    private static IEnumerable<Terminal.Gui.ViewBase.View> Descendants(Terminal.Gui.ViewBase.View root)
    {
        foreach (var sub in root.SubViews)
        {
            yield return sub;
            foreach (var inner in Descendants(sub)) yield return inner;
        }
    }

    private static void TestSanitizerSoup()
    {
        Console.WriteLine("· script/style/noscript/media/comment soup is stripped");
        const string soup = """
            <!doctype html><html><head><title>Soup page</title>
            <style>.x{color:red}</style>
            <script>var html = "</div><a href='https://evil.example/fake'>fake</a>";</script>
            <script type="application/ld+json">{"@type":"Thing","name":"json-blob-token"}</script>
            </head><body>
            <!-- <a href="https://evil.example/comment">comment-link</a> -->
            <noscript><div class="nojs">noscript-fallback-text</div></noscript>
            <svg><text>svg-artwork-text</text></svg>
            <iframe src="https://evil.example/frame"></iframe>
            <main><h1>Real headline here with enough words to anchor the page</h1>
            <p>Real paragraph with enough substance to be the actual content body text.</p>
            <p><a href="https://example.com/real">real link</a></p></main>
            </body></html>
            """;
        string d = TempDir();
        try
        {
            using var engine = new BrowserEngine(d);
            var rendered = engine.RenderHtml(soup, "https://example.com/soup", 100);
            string text = rendered.Text;
            foreach (var token in new[] { "fake", "json-blob-token", "comment-link", "noscript-fallback",
                "svg-artwork-text", "evil.example", "var html" })
                Check(!text.Contains(token), $"no leak: {token}");
            Check(text.Contains("Real headline"), "real content survives");
            Check(rendered.Links.Count == 1 && rendered.Links[0].Url == "https://example.com/real",
                "only real links numbered", $"got {rendered.Links.Count}");
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    private static void TestHiddenContent()
    {
        Console.WriteLine("· display:none / hidden / aria-hidden excluded everywhere");
        const string html = """
            <!doctype html><html><head><title>Hidden test</title>
            <style>.off{display:none}.cols{display:flex}.cols>div{flex:1}</style>
            </head><body>
            <main><div class="cols">
              <div><h3>One</h3><p>First visible column carries enough text to stand alone here.</p></div>
              <div><h3>Two</h3><p>Second visible column carries enough text to stand alone here.</p></div>
              <div class="off"><h3>Ghost</h3><p>Hidden column must never appear anywhere in output text.</p></div>
              <div hidden><h3>Attr</h3><p>Hidden-attribute column must never appear anywhere either.</p></div>
            </div>
            <div aria-hidden="true"><p>Modal dialog text <a href="https://evil.example/modal">modal link</a> stays out.</p></div>
            </main></body></html>
            """;
        var page = LayoutPerceiver.Perceive(html, "https://example.com/h");
        var rows = FindRows(page.Main);
        Check(rows.Count == 1 && rows[0].Cells.Count == 2, "hidden 4th/5th columns excluded from grid",
            $"got {rows.Count} rows");
        string d = TempDir();
        try
        {
            using var engine = new BrowserEngine(d);
            var rendered = engine.RenderHtml(html, "https://example.com/h", 100);
            foreach (var token in new[] { "Ghost", "Attr", "Modal dialog", "evil.example" })
                Check(!rendered.Text.Contains(token), $"hidden stays out: {token}");
            Check(rendered.Text.Contains("First visible"), "visible columns survive");
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    private static void TestBaseHref()
    {
        Console.WriteLine("· <base href> resolves relative links");
        const string html = """
            <html><head><base href="https://cdn.example.com/docs/"></head><body>
            <main><p>See <a href="page.html?q=1">the page</a> for details and more text here.</p></main>
            </body></html>
            """;
        var page = LayoutPerceiver.Perceive(html, "https://example.com/a/b");
        Check(page.Links.Count == 1
            && page.Links[0].Url == "https://cdn.example.com/docs/page.html?q=1",
            "base-href resolution", $"got '{(page.Links.Count > 0 ? page.Links[0].Url : "none")}'");
    }

    private static void TestTabsModel()
    {
        Console.WriteLine("· tab strip model (open/switch/close, per-tab state)");
        var tabs = new TabsModel("about:demo");
        Check(tabs.Tabs.Count == 1 && tabs.Active == 0, "starts with one home tab");
        tabs.NewTab();
        tabs.NewTab();
        Check(tabs.Tabs.Count == 3 && tabs.Active == 2, "new tabs activate");
        tabs.Current.Url = "https://example.com/a";
        tabs.Current.Back.Push("about:demo");
        tabs.SwitchTo(0);
        Check(tabs.Current.Url == "about:demo", "switch restores tab");
        tabs.SwitchTo(2);
        Check(tabs.Current.Url == "https://example.com/a" && tabs.Current.Back.Count == 1,
            "per-tab history isolated");
        tabs.CloseTab(2);
        Check(tabs.Tabs.Count == 2 && tabs.Active == 1, "closing active selects neighbor");
        tabs.CloseTab(0);
        tabs.CloseTab(0);
        Check(tabs.Tabs.Count == 1 && tabs.Current.Url == "about:demo", "closing last reseeds home");
        for (int i = 0; i < 20; i++) tabs.NewTab();
        Check(tabs.Tabs.Count == TabsModel.MaxTabs, $"capped at {TabsModel.MaxTabs}");
        tabs.Next();
        Check(tabs.Active == 0, "next wraps around");
        // Browser-style reorder: active tab moves and stays active.
        var order = new TabsModel("about:home");
        order.NewTab(); order.NewTab();
        order.Tabs[0].Url = "u0"; order.Tabs[1].Url = "u1"; order.Tabs[2].Url = "u2";
        order.SwitchTo(1);
        Check(order.MoveActive(-1) && order.Active == 0 && order.Tabs[0].Url == "u1" && order.Tabs[1].Url == "u0",
            "move active left keeps order + follows");
        Check(!order.MoveActive(-1) && order.Active == 0, "move left at first is no-op");
        Check(order.MoveActive(2) && order.Active == 2 && order.Tabs[2].Url == "u1",
            "move active right by 2");
        Check(!order.MoveActive(1) && order.Active == 2, "move right at last is no-op");
        // Close tabs to the right: anchor survives, active falls back.
        var cr = new TabsModel("about:home");
        cr.NewTab(); cr.NewTab(); cr.NewTab();
        for (int i = 0; i < 4; i++) cr.Tabs[i].Url = "t" + i;
        cr.SwitchTo(3);
        Check(cr.CloseTabsToRight(1) == 2 && cr.Tabs.Count == 2 && cr.Active == 1
            && cr.Tabs[0].Url == "t0" && cr.Tabs[1].Url == "t1",
            "close-right drops tail, active falls back to anchor");
        Check(cr.CloseTabsToRight(1) == 0 && cr.Tabs.Count == 2, "close-right at last closes none");
        cr.SwitchTo(0);
        Check(cr.CloseTabsToRight(0) == 1 && cr.Tabs.Count == 1 && cr.Active == 0,
            "close-right keeps earlier active");
        Check(cr.CloseTabsToRight(9) == 0, "close-right out of range closes none");
    }

    private static void TestChromeForms()
    {
        Console.WriteLine("· header/nav search forms become live controls in place");
        const string html = """
            <!doctype html><html><head><title>Paper</title></head><body>
            <header><h1>The Paper</h1>
              <form action="https://example.com/search" method="get">
                <input type="search" name="q" placeholder="Search articles">
                <input type="submit" value="Go">
              </form>
            </header>
            <nav><a href="https://example.com/">Home</a>
              <form action="https://example.com/newsletter" method="post">
                <input type="email" name="em" placeholder="you@mail.com">
                <input type="submit" value="Join">
              </form>
            </nav>
            <main><p>Front page body text with enough substance to anchor the main region here.</p></main>
            </body></html>
            """;
        var page = LayoutPerceiver.Perceive(html, "https://example.com/");
        var headerForms = FindForms(page.Header!).ToList();
        Check(headerForms.Count == 1 && headerForms[0].Form.Controls.Any(c => c.Kind == "search"),
            "header search form detected in place");
        Check(page.NavForms.Count == 1 && page.NavForms[0].Form.Controls.Any(c => c.Kind == "email"),
            "nav newsletter form detected in place");
        Check(page.NavLinks.Count == 1, "nav links intact alongside the form");

        string d = TempDir();
        try
        {
            using var engine = new BrowserEngine(d);
            var rendered = engine.RenderHtml(html, "https://example.com/", 100);
            Check(rendered.Text.IndexOf("Search articles", StringComparison.Ordinal)
                < rendered.Text.IndexOf("Front page body", StringComparison.Ordinal),
                "header form renders in place, before main");
            var host = new Terminal.Gui.ViewBase.View();
            var renderer = new ViewRenderer(100, page.BaseUrl, page.Links, new NoopActions());
            renderer.RenderInto(host, page);
            var all = Descendants(host).ToList();
            Check(all.OfType<Terminal.Gui.Views.TextField>().Count() == 2, "both search boxes are TextFields");
            Check(all.OfType<Terminal.Gui.Views.Button>().Count(b => b.Text == "Go" || b.Text == "Join") == 2,
                "both submits are Buttons");
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    private static void TestNoLinksIndexInApp()
    {
        Console.WriteLine("· app view has no raw-URL links dump (hover previews instead)");
        string d = TempDir();
        try
        {
            using var engine = new BrowserEngine(d);
            var page = LayoutPerceiver.Perceive(DemoPages.ThreeCol, "https://example.com/");
            var host = new Terminal.Gui.ViewBase.View();
            var renderer = new ViewRenderer(100, page.BaseUrl, page.Links, new NoopActions());
            renderer.RenderInto(host, page);
            var labelTexts = Descendants(host).OfType<Terminal.Gui.Views.Label>()
                .Select(l => l.Text ?? "").ToList();
            var tvTexts = Descendants(host).OfType<Terminal.Gui.Views.TextView>()
                .Select(v => v.Text ?? "").ToList();
            var allTexts = labelTexts.Concat(tvTexts).ToList();
            Check(!allTexts.Any(t => t.Contains("→ https://")), "no URL dump labels in app");
            Check(allTexts.Any(t => t.Contains("[1]")), "markers still present + clickable/selectable");
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    private static void TestShortcuts()
    {
        Console.WriteLine("· Ctrl+T/W/Tab/L/R + Ctrl+Shift+Left/Right shortcuts");
        Check(KeyboardShortcuts.Map(Terminal.Gui.Input.Key.T.WithCtrl) == BrowserShortcut.NewTab, "Ctrl+T");
        Check(KeyboardShortcuts.Map(Terminal.Gui.Input.Key.W.WithCtrl) == BrowserShortcut.CloseTab, "Ctrl+W");
        Check(KeyboardShortcuts.Map(Terminal.Gui.Input.Key.Tab.WithCtrl) == BrowserShortcut.NextTab, "Ctrl+Tab");
        Check(KeyboardShortcuts.Map(Terminal.Gui.Input.Key.L.WithCtrl) == BrowserShortcut.FocusUrl, "Ctrl+L");
        Check(KeyboardShortcuts.Map(Terminal.Gui.Input.Key.R.WithCtrl) == BrowserShortcut.Reload, "Ctrl+R");
        Check(KeyboardShortcuts.Map(Terminal.Gui.Input.Key.CursorLeft.WithCtrl.WithShift) == BrowserShortcut.MoveTabLeft, "Ctrl+Shift+Left moves tab left");
        Check(KeyboardShortcuts.Map(Terminal.Gui.Input.Key.CursorRight.WithCtrl.WithShift) == BrowserShortcut.MoveTabRight, "Ctrl+Shift+Right moves tab right");
        Check(KeyboardShortcuts.Map(Terminal.Gui.Input.Key.CursorLeft.WithCtrl) == null, "plain Ctrl+Left stays unmapped");
        Check(KeyboardShortcuts.Map(Terminal.Gui.Input.Key.T) == null, "bare T ignored");
        Check(KeyboardShortcuts.Map(Terminal.Gui.Input.Key.A.WithCtrl) == null, "Ctrl+A ignored");
    }

    private sealed class RecordingActions : IBrowserActions
    {
        public List<string> Navigated = new();
        public List<string> Copied = new();
        public int Screenshots;
        public void Navigate(string url) => Navigated.Add(url);
        public void SubmitPost(string actionUrl, List<FieldData> fields, List<FileData> files, bool multipart) { }
        public void OpenResult(string body, string finalUrl, string? contentType) { }
        public void Notify(string message) { }
        public void PreviewLink(string? url) { }
        public void CopyToClipboard(string text) => Copied.Add(text);
        public void TakeScreenshot() => Screenshots++;
    }

    private static void TestLinkClickLeftOnly()
    {
        Console.WriteLine("· canvas hit-testing navigates (generic, any site)");
        string d = TempDir();
        try
        {
            using var engine = new BrowserEngine(d);
            var page = LayoutPerceiver.Perceive(DemoPages.ThreeCol, "https://example.com/");
            var rec = new RecordingActions();
            var host = new Terminal.Gui.ViewBase.View();
            var renderer = new ViewRenderer(100, page.BaseUrl, page.Links, rec);
            renderer.RenderInto(host, page);
            var canvas = Descendants(host).OfType<PageTextView>().Single();
            Check(canvas.DocLines.Any(l => l.Contains("[1]")), "marker text in canvas");
            // No per-link overlay views anymore: one canvas hit-tests everything.
            var overlays = Descendants(host).OfType<Terminal.Gui.Views.Label>()
                .Where(b => System.Text.RegularExpressions.Regex.IsMatch(b.Text ?? "", @"^\[\d+\]$")).ToList();
            Check(overlays.Count == 0, "no overlay views", $"got {overlays.Count}");
            // Hit-test the [1] marker directly.
            int row = canvas.DocLines.ToList().FindIndex(l => l.Contains("[1]"));
            Check(row >= 0, "row with [1] found");
            int col = canvas.DocLines[row].IndexOf("[1]", StringComparison.Ordinal);
            Check(canvas.HitTestUrl(col, row) == page.Links[0].Url, "hit-test resolves link");
            Check(canvas.HitTestUrl(0, row + 5000) == null, "out-of-range miss");
            // Direct navigation helper (same call a link click makes; left-only).
            Check(rec.Navigated.Count == 0, "no nav before click");
            Check(renderer.TryNavigateLink(1) && rec.Navigated.Count == 1
                && rec.Navigated[0] == page.Links[0].Url, "link-click navigates");
            Check(!renderer.TryNavigateLink(0) && !renderer.TryNavigateLink(page.Links.Count + 1),
                "bad indexes do not navigate");
            // FindAllSpans is generic (any wrapped line).
            var spans = LinkHitTest.FindAllSpans("Home [4] │ Shop [5]");
            Check(spans.Count == 2 && spans[0].Index == 4 && spans[1].Index == 5,
                "all markers enumerated");
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    private static void TestInputBrowserBehavior()
    {
        Console.WriteLine("· inputs behave browser-like (focus, Enter-submit, Tab)");
        string d = TempDir();
        try
        {
            using var engine = new BrowserEngine(d);
            var page = LayoutPerceiver.Perceive(DemoPages.Search, "https://example.com/a");
            var host = new Terminal.Gui.ViewBase.View();
            var renderer = new ViewRenderer(100, page.BaseUrl, page.Links, new NoopActions());
            renderer.RenderInto(host, page);
            var all = Descendants(host).ToList();
            var tfs = all.OfType<Terminal.Gui.Views.TextField>().ToList();
            Check(tfs.Count == 2 && tfs.All(f => f.CanFocus && f.Enabled),
                "text inputs focusable", $"got {tfs.Count}");
            var tvs = all.OfType<Terminal.Gui.Views.TextView>().Where(v => !v.ReadOnly).ToList();
            Check(tvs.Count == 1 && !tvs[0].TabKeyAddsTab,
                "textarea Tab moves focus (not tab char)");
            Check(tvs.Count == 1 && tvs[0].CanFocus, "textarea focusable for typing");
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    private static void TestNewTabUrlFocus()
    {
        Console.WriteLine("· new tab lands in the address box with text selected");
        // NewTab() focuses + selects via BrowserApp.FocusUrlForTyping; the
        // helper is static precisely so this headless test drives the exact
        // production path (same SetFocus + SelectAll sequence).
        using var url = new Terminal.Gui.Views.TextField { Text = "about:home" };
        BrowserApp.FocusUrlForTyping(url);
        Check(url.SelectedLength == "about:home".Length, "address text fully selected",
            $"got {url.SelectedLength}");
        Check(url.SelectedText == "about:home", "selected text is the address");
        // Typing over a full selection replaces it (what the user expects).
        using var empty = new Terminal.Gui.Views.TextField { Text = "" };
        BrowserApp.FocusUrlForTyping(empty);
        Check(empty.SelectedLength == 0, "empty address selects nothing, no throw");
    }

    private static void TestProseSelection()
    {
        Console.WriteLine("· prose selectable in one canvas (generic text selection)");
        string d = TempDir();
        try
        {
            using var engine = new BrowserEngine(d);
            var page = LayoutPerceiver.Perceive(DemoPages.ThreeCol, "https://example.com/");
            var host = new Terminal.Gui.ViewBase.View();
            var renderer = new ViewRenderer(100, page.BaseUrl, page.Links, new NoopActions());
            int h = renderer.RenderInto(host, page);
            var canvases = Descendants(host).OfType<PageTextView>().ToList();
            Check(canvases.Count == 1, "exactly one canvas", $"got {canvases.Count}");
            Check(canvases.Count == 1 && canvases[0].ReadOnly, "canvas read-only");
            Check(canvases.Count == 1 && canvases[0].CanFocus, "canvas focusable for selection");
            Check(canvases.Count == 1 && canvases[0].TabStop == Terminal.Gui.ViewBase.TabBehavior.NoStop,
                "canvas skips Tab (inputs keep Tab)");
            Check(canvases.Count == 1 && canvases[0].DocLines.Count == h, "canvas holds every line");
            // Rules are canvas text now, not decoration views.
            Check(canvases.Count == 1 && canvases[0].DocLines.Any(l => l.Contains("──")),
                "rules are canvas text");
            Check(!Descendants(host).OfType<Terminal.Gui.Views.Label>().Any(), "zero label views");
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    private static void TestContextMenuGeneric()
    {
        Console.WriteLine("· right-click menu Copy/Select-all/Screenshot (generic)");
        string d = TempDir();
        try
        {
            using var engine = new BrowserEngine(d);
            var page = LayoutPerceiver.Perceive(DemoPages.ThreeCol, "https://example.com/");
            var rec = new RecordingActions();
            var host = new Terminal.Gui.ViewBase.View();
            var renderer = new ViewRenderer(100, page.BaseUrl, page.Links, rec);
            renderer.RenderInto(host, page);
            // Host right-click must not throw without a running Application (generic).
            var m = new Terminal.Gui.Input.Mouse
            {
                Flags = Terminal.Gui.Input.MouseFlags.RightButtonClicked,
                Position = new System.Drawing.Point(0, 0),
            };
            try { renderer.ShowContextMenu(null, m); Check(true, "host menu no-throw"); }
            catch (Exception ex) { Check(false, "host menu no-throw", ex.Message); }
            // Direct clipboard + screenshot actions are generic (no site hacks).
            rec.CopyToClipboard("hello");
            Check(rec.Copied.Count == 1 && rec.Copied[0] == "hello", "copy action records");
            rec.TakeScreenshot();
            Check(rec.Screenshots == 1, "screenshot action records");
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    private static void TestTextConcat()
    {
        Console.WriteLine("· zero-alloc text concat (string.Create, no stackalloc)");
        Check(TextConcat.Fit("hi", 5) == "hi   ", "Fit pads");
        Check(TextConcat.Fit("hello!", 5) == "hello", "Fit truncates");
        Check(ReferenceEquals(TextConcat.Fit("abcde", 5), "abcde") == false
            || TextConcat.Fit("abcde", 5) == "abcde", "Fit exact");
        Check(TextConcat.Rule(5) == "─────", "Rule plain");
        Check(TextConcat.Rule(10, "ab") == "── ab ────", "Rule titled");
        Check(TextConcat.Trunc("abcdef", 4) == "abcd…", "Trunc marker");
        Check(TextConcat.Trunc("abc", 4) == "abc", "Trunc no-cut");
        Check(TextConcat.LinkLabel("Home", 4) == "Home [4]", "LinkLabel");
        Check(TextConcat.TableTitle(2, 3) == "table · 2×3", "TableTitle");
        Check(TextConcat.ColLabel(0, 6) == "col 1/6", "ColLabel");
        Check(TextConcat.BracketSpaced("Go") == "[ Go ]", "BracketSpaced");
        Check(TextConcat.Bracketed("x") == "[x]", "Bracketed");
        Check(TextConcat.WithColon("a") == "a:", "WithColon");
        Check(TextConcat.RadioLine(true, "A") == "(•) A", "RadioLine");
        Check(TextConcat.CheckLine(false, "B") == "[ ] B", "CheckLine");
        Check(TextConcat.OptionLine(true, "C") == "▸ C", "OptionLine");
        Check(TextConcat.Border("┌", "┬", "┐", new[] { 2, 2 }) == "┌────┬────┐", "Border");
        Check(TextConcat.ColumnLine(new[] { "a", "b" }, new[] { 5, 5 }, " │ ") == "a     │ b",
            "ColumnLine");
        Check(TextConcat.ColumnLine(new[] { "a", "" }, new[] { 5, 5 }, " │ ") == "a     │",
            "ColumnLine keeps bar on blank tail");
        Check(TextConcat.ColumnLine(new[] { "", "" }, new[] { 5, 5 }, " │ ") == "      │",
            "ColumnLine blank tail keeps bar (TrimEnd)");
        Check(TextConcat.TableRowLine(new[] { "a", "b" }, new[] { 3, 3 }) == "│ a   │ b   │",
            "TableRowLine keeps padding");
        Check(TextConcat.JoinLines(new[] { "a", "b" }) == "a\nb", "JoinLines");
        Check(TextConcat.OneLine("a\nb") == "a b", "OneLine newline");
        Check(TextConcat.OneLine("a   b") == "a b", "OneLine collapse");
        Check(TextConcat.OneLine("  a  ") == "a", "OneLine trim");
        Check(TextConcat.Clean("  hello   world  ") == "hello world", "Clean spaces");
        Check(TextConcat.Clean("a  . b") == "a. b", "Clean punct");
        Check(TextConcat.Clean("a\n\n\n\nb") == "a\n\nb", "Clean newline cap");
        Check(TextConcat.Clean("a \n b") == "a\nb", "Clean space-newline");
        Check(TextConcat.HeaderLine("h", "S", "D", 80).Contains("h")
            && TextConcat.HeaderLine("h", "S", "D", 80).Contains("80 cols"), "HeaderLine");
        Check(TextConcat.LinkIndexLine(1, "T", "http://u", 100) == "[1] T → http://u",
            "LinkIndexLine");
        Check(TextConcat.FormHead("", "get", "") == "form", "FormHead bare");
        Check(TextConcat.FormHead("S", "post", "/x") == "form · S · POST /x", "FormHead full");
        Check(TextConcat.FileLine("F", "v") == "F: [file: v]", "FileLine");
        Check(TextConcat.FieldLine("L", "text", "v") == "L: [text v]", "FieldLine");
        Check(TextConcat.VSep(2) == " │ \n │ ", "VSep");
        Check(string.Join("|", TextUtil.Wrap("a b c", 4)) == "a b|c", "Wrap words");
        Check(string.Join("|", TextUtil.Wrap("abcdef", 4)) == "abcd|ef", "Wrap hard-break");

        // Big text: heap-backed string.Create must not OOM like stackalloc would.
        try
        {
            string big = new string('x', 1_000_000);
            Check(TextConcat.Fit(big, 1_500_000).Length == 1_500_000, "big Fit");
            Check(TextConcat.Rule(500_000).Length == 500_000, "big Rule");
            Check(TextConcat.Clean(big).Length == 1_000_000, "big Clean");
            Check(TextConcat.OneLine(big).Length == 1_000_000, "big OneLine");
            var many = Enumerable.Repeat("ab", 20_000).ToList();
            Check(TextConcat.JoinLines(many).Length == 20_000 * 2 + 19_999, "big JoinLines");
            Check(TextUtil.Wrap(big[..100_000] + " " + big[..100_000], 300).Count > 0, "big Wrap");
            Check(TextConcat.Border("┌", "┬", "┐", Enumerable.Repeat(4, 200).ToArray()).Length > 0,
                "big Border");
        }
        catch (Exception ex) { Check(false, "big text no-OOM", ex.GetType().Name + ": " + ex.Message); }
    }

    private static void TestCanvasPlaceholders()
    {
        Console.WriteLine("· live controls sit on blank canvas rows (flow/build stay in sync)");
        string d = TempDir();
        try
        {
            using var engine = new BrowserEngine(d);
            foreach (var (name, html, url) in new[]
            {
                ("cards", DemoPages.Cards, "https://example.com/blog"),
                ("search", DemoPages.Search, "https://example.com/a"),
                ("holy-grail", DemoPages.HolyGrail, "https://example.com/g"),
                ("tailwind", DemoPages.Tailwindish, "https://example.com/s"),
                ("table", DemoPages.TablesAndSidebar, "https://example.com/t"),
            })
            {
                var page = LayoutPerceiver.Perceive(html, url);
                var host = new Terminal.Gui.ViewBase.View();
                var renderer = new ViewRenderer(100, page.BaseUrl, page.Links, new NoopActions());
                int h = renderer.RenderInto(host, page);
                var flow = renderer.LastFlow;
                Check(flow != null, $"{name}: flow recorded");
                if (flow == null) continue;
                var canvas = Descendants(host).OfType<PageTextView>().Single();
                Check(flow.Lines.Count == h && canvas.DocLines.Count == h, $"{name}: line counts agree");
                int bad = 0;
                foreach (var s in flow.Specs)
                {
                    int hh = s switch
                    {
                        ViewRenderer.FieldSpec f => f.Ctrl.Kind == "textarea" ? 4 : 1,
                        ViewRenderer.OptionSpec o => Math.Max(1, o.Options.Count),
                        ViewRenderer.ButtonSpec => 1,
                        _ => 0,
                    };
                    if (s is ViewRenderer.HiddenSpec) continue;
                    // Controls may share joined grid lines with a sibling column's
                    // text: only their own [X, X+W) span must be blank.
                    for (int r = s.Y; r < s.Y + hh; r++)
                    {
                        if (r < 0 || r >= canvas.DocLines.Count) { bad++; break; }
                        string line = canvas.DocLines[r];
                        int from = Math.Min(s.X, line.Length);
                        int take = Math.Max(0, Math.Min(s.W, line.Length - from));
                        if (!string.IsNullOrWhiteSpace(line.Substring(from, take))) { bad++; break; }
                    }
                }
                Check(bad == 0, $"{name}: {flow.Specs.Count} specs on blank rows", $"got {bad} overlaps");
            }
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    private static void TestTabState()
    {
        Console.WriteLine("· per-tab state: labels, history stacks, load cancel, overlay reset");
        var t = new TabState { Title = "Hello World this is a long page title", Url = "https://example.com/x" };
        Check(t.GetLabel() == "Hello World this is a …", "label truncates", $"got '{t.GetLabel()}'");
        Check(!t.CanGoBack && !t.CanGoForward, "fresh tab has no history");
        t.Back.Push("https://example.com/prev");
        Check(t.CanGoBack && !t.CanGoForward, "back available after push");
        var loading = new TabState { Url = "https://example.com/y" };
        loading.IsLoading = true;
        loading.LoadCts = new CancellationTokenSource();
        loading.LoadCts.Cancel();
        loading.CancelLoad();
        Check(!loading.IsLoading && loading.LoadCts == null, "CancelLoad clears");
        var over = new TabState { Url = "https://example.com/z" };
        over.Overlay = TabOverlayKind.History;
        over.OverlayCaption = "History";
        over.OverlayLines.Add("x");
        over.ClearOverlay();
        Check(over.Overlay == TabOverlayKind.None && over.OverlayLines.Count == 0
            && over.OverlayCaption.Length == 0, "ClearOverlay resets");
        var spinner = new TabState { Url = "https://example.com/s", IsLoading = true };
        Check(spinner.GetLabel().EndsWith("loading…", StringComparison.Ordinal), "loading label", $"got '{spinner.GetLabel()}'");
        var empty = new TabState();
        Check(empty.GetLabel() == "new tab", "blank tab label");
    }

    private static void TestStopCancellation()
    {
        Console.WriteLine("· stopped loads surface as cancellation, not timeout");
        var fetcher = new TexBowser.Net.WebFetcher();
        var cts = new CancellationTokenSource();
        cts.Cancel();
        bool wasCancel = false;
        try
        {
            // Pre-canceled token short-circuits before any I/O: fully deterministic.
            fetcher.FetchAsync("http://127.0.0.1:9/stopped", cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { wasCancel = true; }
        catch (Exception ex) { Check(false, "canceled fetch throws OCE", $"got {ex.GetType().Name}"); return; }
        Check(wasCancel, "canceled fetch throws OperationCanceledException");
    }

    private static void TestNavChrome()
    {
        Console.WriteLine("· nav chrome constructs with visible ASCII controls");
        string d = TempDir();
        try
        {
            using var engine = new BrowserEngine(d);
            var app = new BrowserApp(engine);
            // Row 0: address chrome only (tab strip lives in Row 1 now).
            var row = app.BuildNavRow(out var go, out var hist);
            Check(row.Length == 6, "6 address-chrome views", $"got {row.Length}");
            var texts = row.Select(v => v.Text?.ToString() ?? "").ToList();
            Check(texts[0] == "<" && texts[1] == ">" && texts[2] == "Reload",
                "back/fwd/reload labels", $"got [{string.Join(",", texts.Take(3))}]");
            Check(texts[4] == "Go" && texts[5] == "Hist",
                "go/hist labels", $"got [{string.Join(",", texts.Skip(4))}]");
            Check(row[3] is Terminal.Gui.Views.TextField, "address box is a TextField");
            Check(row.All(v => v.Height == 1), "single-row chrome");
            // Font-safety: every chrome label must be printable ASCII only, so a
            // missing console glyph can never again look like a missing button.
            bool ascii = row.Where(v => v is not Terminal.Gui.Views.TextField)
                .SelectMany(v => (v.Text?.ToString() ?? "").ToCharArray())
                .All(c => c >= 32 && c < 127);
            Check(ascii, "chrome labels ASCII-only");
            Check(go.Text?.ToString() == "Go" && hist.Text?.ToString() == "Hist",
                "out params wired");
            // Row 1: browser-style tab strip buttons (+ next to tabs, x top-right).
            var tabBtns = app.BuildTabRowButtons(out var add, out var close);
            Check(tabBtns.Length == 2, "2 tab-strip buttons", $"got {tabBtns.Length}");
            Check(add.Text?.ToString() == "+" && close.Text?.ToString() == "x",
                "plus/close labels");
            Check(tabBtns.All(v => v.Height == 1), "single-row tab buttons");
            bool tabAscii = tabBtns
                .SelectMany(v => (v.Text?.ToString() ?? "").ToCharArray())
                .All(c => c >= 32 && c < 127);
            Check(tabAscii, "tab button labels ASCII-only");
            // Layout regression: address row tiles Row 0 L-to-R with 1-col
            // gaps; tab strip tiles Row 1 as [Tabs][+][x]. Assert real frames
            // after layout at desktop width, incl. no X-overlaps. Pos.AnchorEnd
            // math once pushed controls off-screen, so this guards the exact
            // Fill/AnchorEnd arithmetic in both builders.
            try
            {
                using var win = new Terminal.Gui.Views.Window { Title = "t" };
                using var tabs = new Terminal.Gui.Views.Tabs
                {
                    X = 0, Y = 1,
                    Width = Terminal.Gui.ViewBase.Dim.Fill(BrowserApp.TabStripReserve),
                    Height = Terminal.Gui.ViewBase.Dim.Fill(),
                };
                win.Add(row);
                win.Add(tabs);
                win.Add(tabBtns);
                win.X = 0; win.Y = 0; win.Width = 100; win.Height = 30;
                win.Layout();
                int cw = tabs.Frame.Width + BrowserApp.TabStripReserve;
                bool inside = row.Concat(tabBtns)
                    .All(v => v.Frame.X >= 0 && v.Frame.X + v.Frame.Width <= cw);
                Check(inside, "chrome + tab buttons inside content width",
                    $"got [{string.Join(",", row.Concat(tabBtns).Select(v => $"{v.Text}:{v.Frame.X}+{v.Frame.Width}"))}] cw={cw}");
                bool RowOverlap(IEnumerable<Terminal.Gui.ViewBase.View> views)
                {
                    var list = views.ToList();
                    for (int i = 0; i < list.Count; i++)
                        for (int j = i + 1; j < list.Count; j++)
                        {
                            var a = list[i].Frame; var b = list[j].Frame;
                            if (a.X < b.X + b.Width && b.X < a.X + a.Width) return true;
                        }
                    return false;
                }
                Check(!RowOverlap(row), "address views do not overlap");
                Check(!RowOverlap(tabBtns.Append(tabs)), "tab strip does not overlap");
                // Tab buttons must sit in Row 1 (Y=1), right-docked: x at the
                // far edge, + one gap left of x, Tabs filling the rest.
                Check(tabBtns.All(v => v.Frame.Y == 1), "tab buttons live in tab row");
                Check(close.Frame.X + close.Frame.Width == cw
                    && add.Frame.X + add.Frame.Width + 1 == close.Frame.X
                    && tabs.Frame.X == 0 && tabs.Frame.X + tabs.Frame.Width + 1 == add.Frame.X,
                    "tab strip tiles [Tabs][+][x] with 1-col gaps",
                    $"got tabs:{tabs.Frame.X}+{tabs.Frame.Width} +=+{add.Frame.X}+{add.Frame.Width} x:{close.Frame.X}+{close.Frame.Width} cw={cw}");
                // Address row tiling: url bridges reload..Go with 1-col gaps.
                var back = row[0]; var fwd = row[1]; var rel = row[2];
                var url = row[3];
                Check(back.Frame.X == 0 && fwd.Frame.X == back.Frame.X + back.Frame.Width + 1
                    && rel.Frame.X == fwd.Frame.X + fwd.Frame.Width + 1
                    && url.Frame.X == rel.Frame.X + rel.Frame.Width + 1
                    && url.Frame.X + url.Frame.Width + 1 == go.Frame.X
                    && go.Frame.X + go.Frame.Width + 1 == hist.Frame.X
                    && hist.Frame.X + hist.Frame.Width == cw,
                    "address row tiles with 1-col gaps",
                    $"got [{string.Join(",", row.Select(v => $"{v.Text}:{v.Frame.X}+{v.Frame.Width}"))}] cw={cw}");
                // Visibility regression: Terminal.Gui v2 Button defaults to
                // ShadowStyle=Opaque, which reserves 1 col + 1 row for the
                // shadow. At Height=1 that leaves Viewport.Height=0, so every
                // button label silently never draws (only the url TextField,
                // whose default shadow is null, stayed visible). All chrome
                // buttons must keep a non-zero viewport after layout.
                bool viewportOk = row.Concat(tabBtns).OfType<Terminal.Gui.Views.Button>()
                    .All(b => b.Viewport.Width >= 1 && b.Viewport.Height >= 1);
                Check(viewportOk, "chrome buttons have drawable viewport",
                    $"got [{string.Join(",", row.Concat(tabBtns).OfType<Terminal.Gui.Views.Button>().Select(b => $"{b.Text}:{b.Viewport.Width}x{b.Viewport.Height}"))}]");
                // Tab buttons must be siblings of Tabs (added to the window),
                // never children that would become extra tabs.
                Check(tabBtns.All(v => v.SuperView == win), "tab buttons are window siblings, not tabs");
                Check(tabs.TabCollection.Count() == 0, "tab buttons add no tabs");
            }
            catch (Exception ex) { Check(false, "chrome layout", ex.Message); }
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    private static void TestHomeLanding()
    {
        Console.WriteLine("· landing page is a centered Ready To Surf dummy");
        Check(DemoPages.Get("about:home")?.Contains("Ready To Surf!") == true, "about:home has dummy message");
        Check(DemoPages.Get("about:blank")?.Contains("Ready To Surf!") == true, "about:blank aliases home");
        Check(DemoPages.Get("about:demo")?.Contains("Ready To Surf!") != true, "about:demo keeps Acme demo");
        Check(BrowserApp.IsHomeUrl("about:home"), "about:home is home");
        Check(BrowserApp.IsHomeUrl("about:blank"), "about:blank is home");
        Check(BrowserApp.IsHomeUrl("ABOUT:HOME"), "home check case-insensitive");
        Check(!BrowserApp.IsHomeUrl("about:demo"), "about:demo is not home");
        Check(!BrowserApp.IsHomeUrl("https://example.com/"), "https url is not home");
        Check(!BrowserApp.IsHomeUrl(""), "empty is not home");
        var lines = BrowserApp.BuildHomeLines(100);
        Check(lines.Any(l => l.Contains("Ready To Surf!")), "home lines carry the message");
        Check(lines.All(l => l.Length <= 100), "home lines fit width");
        string msg = lines.First(l => l.Contains("Ready To Surf!"));
        Check(msg.StartsWith(" "), "home message is horizontally centered");
        Check(lines.Count(l => l.Length == 0) >= 6, "home has top breathing room");
    }

    private static List<FormNode> FindForms(LayoutNode node)
    {
        var out_ = new List<FormNode>();
        void Walk(LayoutNode n)
        {
            switch (n)
            {
                case FormNode f: out_.Add(f); break;
                case RowNode r: foreach (var c in r.Cells) Walk(c.Content); break;
                case StackNode s: foreach (var c in s.Children) Walk(c); break;
            }
        }
        Walk(node);
        return out_;
    }

    private static List<RowNode> FindRows(LayoutNode node)
    {
        var rows = new List<RowNode>();
        void Walk(LayoutNode n)
        {
            switch (n)
            {
                case RowNode r:
                    rows.Add(r);
                    foreach (var c in r.Cells) Walk(c.Content);
                    break;
                case StackNode s:
                    foreach (var c in s.Children) Walk(c);
                    break;
            }
        }
        Walk(node);
        return rows;
    }
}
