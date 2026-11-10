using Microsoft.Extensions.Logging;
using WaybackViewer.Models;

namespace WaybackViewer.Services;

/// Shared, greppable key=value summaries of what the parser/estimator found, for local diagnosis.
public static class ExtractionLog
{
    public static string Describe(PageMetadata? m) => m is null
        ? "meta=none"
        : $"source={m.Source ?? "-"} conf={m.Confidence:F2} views={Flag(m.Views)} likes={Flag(m.Likes)} comments={Flag(m.Comments)} " +
          $"shares={Flag(m.Shares)} score={Flag(m.Score)} engagement={Flag(m.EngagementScore)} published={(m.PublishedAt.HasValue ? "y" : "n")} author={(m.Author is null ? "n" : "y")}";

    private static string Flag(long? v) => v.HasValue ? v.Value.ToString() : "-";

    /// One Debug line per parsed page, a warning for slow parses, and an Information line when nothing popularity-related was found.
    public static void Page(ILogger log, string key, string origin, PageMetadata? m, int htmlChars, int textChars, double parseMs)
    {
        ExtractionStats.Record(key, m, parseMs);
        if (log.IsEnabled(LogLevel.Debug))
            log.LogDebug("parse.page origin={Origin} key={Key} htmlChars={Html} textChars={Text} parseMs={Ms:F1} {Meta}",
                origin, key, htmlChars, textChars, parseMs, Describe(m));
        if (parseMs > 500)
            log.LogWarning("parse.slow origin={Origin} key={Key} htmlChars={Html} parseMs={Ms:F0}", origin, key, htmlChars, parseMs);
        if (m is null || (!m.Views.HasValue && !m.Likes.HasValue && !m.Comments.HasValue && !m.Shares.HasValue && !m.Score.HasValue && m.EngagementScore is not > 0))
            log.LogInformation("parse.no_popularity origin={Origin} key={Key} htmlChars={Html} published={Pub}", origin, key, htmlChars, m?.PublishedAt.HasValue == true);
    }
}
