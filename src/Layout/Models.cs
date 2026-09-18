using System.Text.RegularExpressions;

namespace TexBowser.Layout;

/// <summary>A numbered hyperlink found while perceiving the page.</summary>
public sealed record LinkInfo(int Index, string Text, string Url);

/// <summary>Assigns stable 1-based numbers to links in render order.</summary>
public sealed class LinkCollector
{
    private readonly List<LinkInfo> _links = new();
    public IReadOnlyList<LinkInfo> Links => _links;

    public int Add(string text, string url)
    {
        text = Clean(text);
        if (text.Length == 0) text = url;
        var link = new LinkInfo(_links.Count + 1, text, url);
        _links.Add(link);
        return link.Index;
    }

    private static string Clean(string s)
    {
        s = Regex.Replace(s ?? "", @"\s+", " ").Trim();
        return s.Length > 80 ? s[..80] : s;
    }
}

/// <summary>One block of prose: optional heading + paragraphs (link markers like [3] embedded).</summary>
public sealed record SectionData(string Heading, List<string> Paragraphs)
{
    public bool IsEmpty => Heading.Length == 0 && Paragraphs.Count == 0;
}

/// <summary>A real data table: headers + row cells (layout tables never reach here).</summary>
public sealed record DataTable(List<string> Headers, List<List<string>> Rows);

// ---------------------------------------------------------------------------
// Recursive layout tree. A page is regions; each region body is a LayoutNode:
// stacked blocks, column rows, leaf text, or an inline data table.
// ---------------------------------------------------------------------------

/// <summary>One node of the detected layout tree.</summary>
public abstract record LayoutNode;

/// <summary>Leaf prose.</summary>
public sealed record TextNode(SectionData Text) : LayoutNode;

/// <summary>A data &lt;table&gt; exactly where it appears in the flow.</summary>
public sealed record DataTableNode(DataTable Table) : LayoutNode;

/// <summary>One column cell: nested content + its width fraction of the row.</summary>
public sealed record CellNode(LayoutNode Content, double Fraction);

/// <summary>A horizontal column row; Source names the winning detector.</summary>
public sealed record RowNode(List<CellNode> Cells, string Source) : LayoutNode
{
    public bool IsEmpty => Cells.Count == 0;
}

/// <summary>Vertical flow of blocks (the default for everything).</summary>
public sealed record StackNode(List<LayoutNode> Children) : LayoutNode;

// ---------------------------------------------------------------------------
// Forms: real interactive controls (rendered as working Terminal.Gui views).
// ---------------------------------------------------------------------------

/// <summary>One &lt;option&gt;.</summary>
public sealed record FormOption(string Value, string Text, bool Selected);

/// <summary>
/// One control. Kind: text,password,search,url,email,tel,number,textarea,
/// select,checkbox,radio,submit,reset,button,image,hidden,file.
/// </summary>
public sealed record FormControl(
    string Kind,
    string Name,
    string Value,
    string Placeholder,
    string Label,
    bool Checked,
    bool Disabled,
    List<FormOption> Options);

/// <summary>A whole &lt;form&gt; (or one anonymous stray control).</summary>
public sealed record FormData(
    string Action,
    string Method,
    string Enctype,
    string Title,
    List<FormControl> Controls);

/// <summary>A form rendered as live controls, in flow, where it appears.</summary>
public sealed record FormNode(FormData Form) : LayoutNode;

/// <summary>
/// What perception produced: regions + a layout tree per region.
/// Header/asides/footer are full trees (text AND forms/tables), so a search
/// box in the header renders as live controls exactly where the site has it.
/// Signature stays compact, e.g. H|N|M3|A|F — now purely descriptive.
/// </summary>
public sealed record PerceivedPage(
    string Title,
    string BaseUrl,
    bool HasHeader,
    LayoutNode? Header,
    bool HasNav,
    List<LinkInfo> NavLinks,
    List<FormNode> NavForms,
    LayoutNode Main,
    int MainCols,
    bool HasAside,
    List<LayoutNode> Asides,
    bool HasFooter,
    LayoutNode? Footer,
    double AsideRatio,
    string AsideRatioSource,
    List<string> RowSources,
    string DetectionSummary,
    IReadOnlyList<LinkInfo> Links)
{
    public string Signature =>
        $"{(HasHeader ? "H" : "-")}|{(HasNav ? "N" : "-")}|M{MainCols}|{(HasAside ? "A" : "-")}|{(HasFooter ? "F" : "-")}";
}

/// <summary>Fully reconstructed page: plain-text lines that fit the viewport width.</summary>
public sealed record RenderedPage(
    string Url,
    string Title,
    string Signature,
    string Summary,
    List<string> Lines,
    IReadOnlyList<LinkInfo> Links,
    PerceivedPage Tree)
{
    public string Text => string.Join("\n", Lines);
}
