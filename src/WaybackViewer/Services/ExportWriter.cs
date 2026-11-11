using System.Net;
using System.Text.Json;

namespace WaybackViewer.Services;

/// One exported capture: metadata plus extracted text (Error is set when the text could not be loaded).
public sealed record ExportRecord(string Url, string Archive, string Timestamp, string? Title, string? Description, string? Text, string? Error);

/// Writes the text/metadata export one record at a time, so the server never holds the whole file in memory
/// and the browser only receives a download stream.
public static class ExportWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string NormalizeFormat(string? format) => format?.Trim().ToLowerInvariant() switch
    {
        "csv" => "csv",
        "json" => "json",
        "txt" => "txt",
        _ => "xls"
    };

    public static string ContentType(string format) => format switch
    {
        "xls" => "application/vnd.ms-excel",
        "json" => "application/json; charset=utf-8",
        "csv" => "text/csv; charset=utf-8",
        _ => "text/plain; charset=utf-8"
    };

    public static string Header(string format) => format switch
    {
        "xls" => "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>" +
                 "th{background:#d9eaf7;font-weight:bold}td,th{border:1px solid #999;padding:4px;vertical-align:top;white-space:pre-wrap}" +
                 "</style></head><body><table><tr><th>URL</th><th>Archive URL</th><th>Timestamp</th><th>Title</th><th>Description</th><th>Text</th><th>Error</th></tr>",
        "json" => "[",
        "csv" => "url,archive_url,timestamp,title,description,text,error",
        _ => ""
    };

    public static string Row(string format, ExportRecord r, bool first) => format switch
    {
        "xls" => "<tr>" + string.Concat(Cells(r).Select(v => "<td>" + WebUtility.HtmlEncode(v ?? "") + "</td>")) + "</tr>",
        "json" => (first ? "\n" : ",\n") + JsonSerializer.Serialize(new
        {
            url = r.Url, archive = r.Archive, ts = r.Timestamp, title = r.Title, description = r.Description, text = r.Text, error = r.Error ?? ""
        }, Json),
        "csv" => "\r\n" + string.Join(',', Cells(r).Select(Csv)),
        _ => "==== " + r.Url + "\nArchive: " + r.Archive + "\nCaptured: " + r.Timestamp +
             "\nTitle: " + r.Title + "\nDescription: " + r.Description + "\nError: " + r.Error + "\n\n" + r.Text + "\n\n"
    };

    public static string Footer(string format) => format switch
    {
        "xls" => "</table></body></html>",
        "json" => "\n]",
        _ => ""
    };

    private static IEnumerable<string?> Cells(ExportRecord r) => [r.Url, r.Archive, r.Timestamp, r.Title, r.Description, r.Text, r.Error];

    private static string Csv(string? v) => "\"" + (v ?? "").Replace("\"", "\"\"") + "\"";
}
