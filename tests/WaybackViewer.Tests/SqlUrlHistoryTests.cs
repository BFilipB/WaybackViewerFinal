using WaybackViewer.Models;
using WaybackViewer.Storage;
using WaybackViewer.Storage.Sql;

namespace WaybackViewer.Tests;

/// URL history is a bounded SQL lookup (never the whole list) in every store.
public class SqlUrlHistoryTests
{
    private static Capture At(string ts, string url, string status = "200") => new(ts, url, "text/html", status);

    private static async Task<IArchiveStore[]> Make()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wbv-hist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var caps = Enumerable.Range(1, 90).Select(i => At($"2020010100{i:0000}", "https://a.com/same"))
            .Append(At("20210101000001", "https://a.com/other")).ToList();
        var stores = new IArchiveStore[]
        {
            new SqliteArchiveStore($"Data Source={Path.Combine(dir, "a.db")}", new HtmlFileStore(Path.Combine(dir, "ha"))),
            new SqlArchiveStore(new SqliteDialect(), $"Data Source={Path.Combine(dir, "b.db")}", new HtmlFileStore(Path.Combine(dir, "hb")))
        };
        foreach (var s in stores)
        {
            await s.InitializeAsync();
            await s.SaveSearchAsync("a.com/", "all", caps, DateTimeOffset.UtcNow);
        }
        return stores;
    }

    [Fact]
    public async Task History_returns_the_newest_captures_of_one_url_up_to_the_limit()
    {
        foreach (var store in await Make())
        {
            var rows = await store.GetUrlHistoryAsync("a.com/", "all", "https://a.com/same", 60);
            Assert.NotNull(rows);
            Assert.Equal(60, rows!.Count);
            Assert.All(rows, r => Assert.Equal("https://a.com/same", r.Capture.Original));
            Assert.Equal(rows.OrderByDescending(r => r.Capture.Timestamp, StringComparer.Ordinal).Select(r => r.Id), rows.Select(r => r.Id));
            Assert.Equal("20200101000090", rows[0].Capture.Timestamp);
        }
    }

    [Fact]
    public async Task History_is_null_for_an_unknown_list_and_empty_for_an_unknown_url()
    {
        foreach (var store in await Make())
        {
            Assert.Null(await store.GetUrlHistoryAsync("nope.com/", "all", "https://a.com/same", 60));
            Assert.Empty((await store.GetUrlHistoryAsync("a.com/", "all", "https://a.com/missing", 60))!);
        }
    }
}
