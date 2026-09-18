using HtmlAgilityPack;
using TexBowser.Layout;
using TexBowser.Render;

namespace TexBowser.Engine;

public static class ContentRegressionTests
{
    private const string BaseUrl = "https://example.com/docs/";

    public static int Run()
    {
        int failures = 0;
        foreach (var (name, test) in new (string, Action)[]
        {
            ("long prose and list items", TestLongProse),
            ("more than forty prose blocks", TestManyBlocks),
            ("more than sixty stack children", TestManyChildren),
            ("mixed text and inline runs", TestMixedContent),
            ("root anchors and linked headings", TestAnchors),
            ("deep tables and forms", TestDeepContent),
            ("structured content inside leaves", TestStructuredLeaves),
            ("more than twelve layout-table rows", TestLayoutRows),
            ("long data-table cells", TestLongCells),
        })
        {
            try
            {
                test();
                Console.WriteLine($"  [PASS] content: {name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"  [FAIL] content: {name}: {ex.Message}");
            }
        }
        return failures;
    }

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    private static HtmlNode ParseNode(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return doc.DocumentNode.ChildNodes.First(n => n.NodeType == HtmlNodeType.Element);
    }

    private static PerceivedPage Perceive(string body) =>
        LayoutPerceiver.Perceive("<html><body><main>" + body + "</main></body></html>", BaseUrl);

    private static string Prose(LayoutNode node) => string.Join("\n", Nodes(node).OfType<TextNode>()
        .SelectMany(t => new[] { t.Text.Heading }.Concat(t.Text.Paragraphs)).Where(t => t.Length > 0));

    private static IEnumerable<LayoutNode> Nodes(LayoutNode node)
    {
        yield return node;
        var children = node switch
        {
            StackNode s => s.Children.AsEnumerable(),
            RowNode r => r.Cells.Select(c => c.Content),
            _ => Enumerable.Empty<LayoutNode>(),
        };
        foreach (var child in children)
            foreach (var descendant in Nodes(child))
                yield return descendant;
    }

    private static void CheckOrder(string text, params string[] tokens)
    {
        int at = 0;
        foreach (string token in tokens)
        {
            int found = text.IndexOf(token, at, StringComparison.Ordinal);
            Check(found >= 0, $"missing or out of order: {token}");
            Check(text.IndexOf(token, found + token.Length, StringComparison.Ordinal) < 0,
                $"duplicated: {token}");
            at = found + token.Length;
        }
    }

    private static void TestLongProse()
    {
        string text = string.Join(" ", Enumerable.Repeat("word", 500)) + " PROSE_END";
        var section = LayoutPerceiver.ExtractSection(ParseNode("<div><p>" + text
            + "</p><ul><li>" + text + "</li></ul></div>"), new LinkCollector(), new Uri(BaseUrl));
        Check(section.Paragraphs.SequenceEqual(new[] { text, "• " + text }), "prose was shortened");
        var page = Perceive("<p>" + text + "</p>");
        foreach (int width in new[] { 40, 70, 110 })
        {
            var rendered = PageRenderer.Render(page, BaseUrl, width);
            Check(rendered.Text.Contains("PROSE_END"), $"prose tail missing at {width}");
            Check(rendered.Lines.All(l => l.Length <= width), $"prose overflow at {width}");
        }
    }

    private static void TestManyBlocks()
    {
        var expected = Enumerable.Range(1, 65).Select(i => $"Block{i:D3}").ToList();
        var section = LayoutPerceiver.ExtractSection(ParseNode("<div>"
            + string.Concat(expected.Select(t => "<p>" + t + "</p>")) + "</div>"),
            new LinkCollector(), new Uri(BaseUrl));
        Check(section.Paragraphs.SequenceEqual(expected), "prose block count or order changed");
    }

    private static void TestManyChildren()
    {
        var expected = Enumerable.Range(1, 75).Select(i => $"Item{i:D3}").ToArray();
        var page = Perceive("<div>"
            + string.Concat(Enumerable.Repeat("<div hidden>HIDDEN_TOKEN</div>", 61))
            + string.Concat(expected.Select(t => "<p>" + t + "</p>")) + "</div>");
        CheckOrder(Prose(page.Main), expected);
        Check(!Prose(page.Main).Contains("HIDDEN_TOKEN"), "hidden children leaked");
    }

    private static void TestMixedContent()
    {
        var page = Perceive("<div>Before <strong>middle</strong> after "
            + "<a href='next'>next</a><span hidden>HIDDEN_TOKEN</span><br>line"
            + "<p>Block</p>Tail <em>end</em></div>");
        Check(Prose(page.Main) == "Before middle after next [1]\nline\nBlock\nTail end",
            "inline runs were split, lost, duplicated, or reordered");
        Check(page.Links.Count == 1 && page.Links[0].Url == BaseUrl + "next", "mixed link missing");
        foreach (int width in new[] { 40, 70, 110 })
        {
            var lines = PageRenderer.RenderNode(page.Main, width);
            CheckOrder(string.Join("\n", lines), "Before", "middle", "after", "next [1]", "line", "Block", "Tail", "end");
            Check(lines.All(l => l.Length <= width), $"mixed content overflow at {width}");
        }
        var grid = Perceive("<div style='display:grid;grid-template-columns:1fr 1fr'>Intro"
            + "<div>Left</div><div>Right</div>Outro</div>");
        CheckOrder(Prose(grid.Main), "Intro", "Left", "Right", "Outro");
    }

    private static void TestAnchors()
    {
        var links = new LinkCollector();
        var section = LayoutPerceiver.ExtractSection(ParseNode("<a href='next'><strong>Go</strong></a>"),
            links, new Uri(BaseUrl));
        Check(section.Paragraphs.SequenceEqual(new[] { "Go [1]" }), "root anchor marker missing");
        Check(links.Links.Count == 1 && links.Links[0].Url == BaseUrl + "next", "root anchor not collected once");
        var page = Perceive("<a href='first'>First</a><h2><a href='heading'>Heading</a></h2>"
            + "<a href='card'><div>Card body</div></a><p><a href='last'>Last</a></p>");
        Check(page.Links.Select(l => l.Url).SequenceEqual(new[] { "first", "heading", "card", "last" }
            .Select(s => BaseUrl + s)), "anchor numbering or URLs changed");
        CheckOrder(Prose(page.Main), "First [1]", "Heading [2]", "Card body [3]", "Last [4]");
    }

    private static void TestDeepContent()
    {
        string content = "<p>Before</p><table><tr><th>Column</th></tr><tr><td>Value</td></tr></table>"
            + "<form><input name='q' value='search'><button>Go</button></form><p>After</p>";
        var shallow = Perceive(content);
        var deep = Perceive(string.Concat(Enumerable.Repeat("<div>", 12)) + content
            + string.Concat(Enumerable.Repeat("</div>", 12)));
        Check(Nodes(deep.Main).OfType<DataTableNode>().Count() == 1, "deep table lost");
        var forms = Nodes(deep.Main).OfType<FormNode>().ToList();
        Check(forms.Count == 1 && forms[0].Form.Controls.Count == 2, "deep form controls lost");
        foreach (int width in new[] { 40, 70, 110 })
            Check(PageRenderer.RenderNode(shallow.Main, width).SequenceEqual(PageRenderer.RenderNode(deep.Main, width)),
                $"wrappers changed content at {width}");
    }

    private static void TestStructuredLeaves()
    {
        var page = Perceive("<ul><li>Before<table><tr><th>Column</th></tr><tr><td>Value</td></tr></table>"
            + "After</li></ul><label>Prompt<input name='q'></label>");
        Check(Nodes(page.Main).OfType<DataTableNode>().Count() == 1, "list table lost");
        Check(Nodes(page.Main).OfType<FormNode>().Single().Form.Controls.Single().Name == "q", "label control lost");
        CheckOrder(Prose(page.Main), "Before", "After", "Prompt");
    }

    private static void TestLayoutRows()
    {
        var expected = Enumerable.Range(1, 15).Select(i => $"LayoutRow{i:D3}").ToArray();
        var page = Perceive("<table>" + string.Concat(expected.Select(t => "<tr><td><p>"
            + t + "</p></td></tr>")) + "</table>");
        Check(Nodes(page.Main).OfType<RowNode>().Count(r => r.Source == "table") == 15, "layout rows dropped");
        CheckOrder(Prose(page.Main), expected);
    }

    private static void TestLongCells()
    {
        string text = string.Join(" ", Enumerable.Repeat("cell", 100)) + " CELL_END";
        var page = Perceive("<table><tr><th>Column</th></tr><tr><td>" + text + "</td></tr></table>");
        var table = Nodes(page.Main).OfType<DataTableNode>().Single().Table;
        Check(table.Rows.Single().Single() == text, "cell text shortened");
        foreach (int width in new[] { 40, 70, 110 })
        {
            var lines = PageRenderer.RenderNode(page.Main, width);
            Check(string.Join("\n", lines).Contains("CELL_END"), $"cell tail missing at {width}");
            Check(lines.All(l => l.Length <= width), $"cell overflow at {width}");
        }
    }
}
