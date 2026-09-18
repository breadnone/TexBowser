using HtmlAgilityPack;

namespace TexBowser.Layout;

/// <summary>Parses one &lt;table&gt; element into headers + rows (data or layout alike).</summary>
public static class TableParser
{
    public static DataTable Parse(HtmlNode table, LinkCollector links, Uri? baseUri)
    {
        var headers = new List<string>();
        var ths = table.SelectNodes(".//th");
        if (ths != null)
            foreach (var th in ths.Take(12))
                headers.Add(HtmlText.CellText(th, links, baseUri));

        var rows = new List<List<string>>();
        var trs = table.SelectNodes(".//tr");
        if (trs != null)
            foreach (var tr in trs.Take(60))
            {
                var cells = tr.SelectNodes("./td|./th");
                if (cells == null || cells.Count == 0) continue;
                // Skip the header row when its cells were already captured above.
                if (ths != null && rows.Count == 0 && cells.All(c => c.Name == "th")) continue;
                rows.Add(cells.Take(12).Select(c => HtmlText.CellText(c, links, baseUri)).ToList());
            }
        rows.RemoveAll(r => r.All(c => c.Length == 0));
        if (headers.Count == 0 && rows.Count > 1
            && rows[0].Count > 1 && rows[0].All(c => c.Length is > 0 and <= 40))
        {
            // Promote a header-looking first row.
            headers = rows[0];
            rows.RemoveAt(0);
        }
        return new DataTable(headers, rows);
    }
}
