using System.Globalization;

namespace WaybackViewer.Models;

/// Display helpers for views (pure, no I/O).
public static class DisplayFormat
{
    /// Wayback timestamps (yyyyMMddHHmmss, possibly shorter) as "yyyy-MM-dd" (date only); unparseable input is returned unchanged.
    public static string Timestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "–";
        var v = value.Trim();
        if (v.Length >= 14 && DateTime.TryParseExact(v[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var full))
            return full.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (v.Length == 8 && DateTime.TryParseExact(v, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return v;
    }

    public static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static string Percent(long done, long total) =>
        total <= 0 ? "" : (done >= total ? 100d : done * 100d / total).ToString("0.#", CultureInfo.InvariantCulture) + "%";

    public static string OperationKindLabel(string kind) => kind switch
    {
        "CaptureLoad" => "Capture list",
        "PagePreload" => "Page preload",
        "WaybackRefresh" => "Wayback refresh",
        _ => kind
    };
}
