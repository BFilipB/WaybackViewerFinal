using System.Text;

namespace WaybackViewer;

public static class ArchiveUrls
{
    public const string Base = "https://web.archive.org";

    /// The normal Wayback viewer page (what "Open" links to).
    public static string Viewer(string timestamp, string original) => $"{Base}/web/{timestamp}/{original}";

    /// Frame-friendly rendering (no Wayback toolbar), used as the fallback iframe target.
    public static string Frame(string timestamp, string original) => $"{Base}/web/{timestamp}if_/{original}";

    /// Raw original bytes as archived (no rewriting) - what we download and store.
    public static string Raw(string timestamp, string original) => $"{Base}/web/{timestamp}id_/{original}";

    /// CDX listing of every unique URL under a prefix.
    /// filter: "html200" (default), "html" (any status), "all" (everything).
    public static string Cdx(string prefix, string filter)
    {
        var sb = new StringBuilder(Base)
            .Append("/cdx/search/cdx?url=").Append(Uri.EscapeDataString(prefix))
            .Append("&matchType=prefix&output=json&fl=timestamp,original,mimetype,statuscode&collapse=urlkey");
        if (filter is "html200" or "html") sb.Append("&filter=mimetype:text/html");
        if (filter == "html200") sb.Append("&filter=statuscode:200");
        return sb.ToString();
    }
}
