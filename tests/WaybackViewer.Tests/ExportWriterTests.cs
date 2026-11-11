using System.Text.Json;
using WaybackViewer.Services;

namespace WaybackViewer.Tests;

public class ExportWriterTests
{
    private static readonly ExportRecord A = new("https://a/1", "https://web.archive.org/web/1/https://a/1", "1", "T<1>", "D", "x\"y", null);
    private static readonly ExportRecord B = new("https://a/2", "https://web.archive.org/web/2/https://a/2", "2", null, null, "", "failed");

    private static string Build(string fmt, params ExportRecord[] rows) =>
        ExportWriter.Header(fmt) + string.Concat(rows.Select((r, i) => ExportWriter.Row(fmt, r, i == 0))) + ExportWriter.Footer(fmt);

    [Theory]
    [InlineData(null, "xls")]
    [InlineData("CSV", "csv")]
    [InlineData("json", "json")]
    [InlineData("txt", "txt")]
    [InlineData("exe", "xls")]
    public void Format_is_normalized(string? input, string expected) => Assert.Equal(expected, ExportWriter.NormalizeFormat(input));

    [Fact]
    public void Json_output_is_a_valid_array_for_zero_one_and_many_rows()
    {
        Assert.Equal(0, JsonDocument.Parse(Build("json")).RootElement.GetArrayLength());
        Assert.Equal(1, JsonDocument.Parse(Build("json", A)).RootElement.GetArrayLength());
        using var doc = JsonDocument.Parse(Build("json", A, B));
        Assert.Equal(2, doc.RootElement.GetArrayLength());
        Assert.Equal("x\"y", doc.RootElement[0].GetProperty("text").GetString());
        Assert.Equal("failed", doc.RootElement[1].GetProperty("error").GetString());
    }

    [Fact]
    public void Csv_quotes_and_escapes_values()
    {
        var csv = Build("csv", A);
        Assert.StartsWith("url,archive_url,timestamp", csv);
        Assert.Contains("\"x\"\"y\"", csv);
    }

    [Fact]
    public void Excel_html_encodes_cells()
    {
        var xls = Build("xls", A);
        Assert.Contains("T&lt;1&gt;", xls);
        Assert.EndsWith("</table></body></html>", xls);
    }
}
