using WaybackViewer.Models;

namespace WaybackViewer.Tests;

public class CaptureFacetsTests
{
    private static StoredCapture Stored(long id, string ts, string url, string mime = "text/html", string status = "200") =>
        new(id, new Capture(ts, url, mime, status), false);

    private static readonly IReadOnlyList<StoredCapture> Sample =
    [
        Stored(1, "20190101000000", "https://example.com/blog/a"),
        Stored(2, "20200101000000", "https://example.com/blog/b"),
        Stored(3, "20200601000000", "https://example.com/about", status: "301"),
        Stored(4, "20210101000000", "https://example.com/img/x.png", mime: "image/png"),
        Stored(5, "20210201000000", "https://example.com/style.css", mime: "text/css"),
        Stored(6, "20210301000000", "https://example.com/", mime: "application/octet-stream")
    ];

    [Theory]
    [InlineData("text/html", "Pages")]
    [InlineData("image/png", "Images")]
    [InlineData("text/css", "Code & styles")]
    [InlineData("application/javascript", "Code & styles")]
    [InlineData("application/pdf", "Documents")]
    [InlineData("application/octet-stream", "Other")]
    public void Category_is_derived_from_mime_type(string mime, string expected) =>
        Assert.Equal(expected, CaptureFacets.CategoryOf(new Capture("20200101000000", "https://a.com/", mime, "200")));

    [Theory]
    [InlineData("https://example.com/blog/post", "/blog")]
    [InlineData("https://example.com/", "/")]
    [InlineData("https://example.com", "/")]
    [InlineData("not a url", "/")]
    public void Section_is_the_first_path_segment(string url, string expected) =>
        Assert.Equal(expected, CaptureFacets.SectionOf(new Capture("20200101000000", url, "text/html", "200")));

    [Fact]
    public void Years_are_counted_in_order()
    {
        var facets = CaptureFacets.Build(Sample);
        Assert.Equal(["2019", "2020", "2021"], facets.Years.Select(y => y.Value));
        Assert.Equal([1, 2, 3], facets.Years.Select(y => y.Count));
    }

    [Fact]
    public void Categories_follow_a_fixed_order_and_skip_empty_ones()
    {
        var facets = CaptureFacets.Build(Sample);
        Assert.Equal(["Pages", "Images", "Code & styles", "Other"], facets.Categories.Select(c => c.Value));
    }

    [Fact]
    public void Sections_are_limited_to_the_twelve_largest()
    {
        var many = Enumerable.Range(0, 30)
            .Select(i => Stored(i + 1, "20200101000000", $"https://example.com/s{i}/page")).ToList();
        Assert.Equal(12, CaptureFacets.Build(many).Sections.Count);
    }

    [Fact]
    public void No_filter_returns_everything_newest_first()
    {
        var result = CaptureFacets.Apply(Sample, new CaptureFilter());
        Assert.Equal(6, result.Count);
        Assert.Equal(6, result[0].Id);
    }

    [Fact]
    public void Filters_combine()
    {
        var result = CaptureFacets.Apply(Sample, new CaptureFilter(Year: "2020", Category: "Pages", Status: "200"));
        Assert.Equal([2L], result.Select(r => r.Id));
    }

    [Fact]
    public void Section_filter_matches_only_that_section()
    {
        var result = CaptureFacets.Apply(Sample, new CaptureFilter(Section: "/blog"));
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Unknown_value_gives_no_results() =>
        Assert.Empty(CaptureFacets.Apply(Sample, new CaptureFilter(Year: "1999")));

    [Fact]
    public void IsActive_reflects_any_value()
    {
        Assert.False(new CaptureFilter().IsActive);
        Assert.True(new CaptureFilter(Status: "200").IsActive);
    }
}

public class FacetEndpointTests
{
    [Fact]
    public void Text_filter_matches_url_case_insensitively_and_saved_counts_are_per_year()
    {
        var list = new List<StoredCapture>
        {
            new(1, new Capture("20190101000000", "https://example.com/Blog/a", "text/html", "200"), true),
            new(2, new Capture("20190201000000", "https://example.com/shop/b", "text/html", "200"), false),
            new(3, new Capture("20200101000000", "https://example.com/blog/c", "text/html", "200"), true)
        };
        Assert.Equal(2, CaptureFacets.Apply(list, new CaptureFilter(Text: "BLOG")).Count);

        var y2019 = CaptureFacets.Build(list).Years.Single(y => y.Value == "2019");
        Assert.Equal(2, y2019.Count);
        Assert.Equal(1, y2019.Saved);
    }

    [Fact]
    public void Text_filter_is_not_overly_literal_for_spacing_or_script()
    {
        var list = new List<StoredCapture>
        {
            new(1, new Capture("20190101000000", "https://example.com/здраво-svet", "text/html", "200"), true),
            new(2, new Capture("20190201000000", "https://example.com/other-page", "text/html", "200"), false)
        };

        Assert.Single(CaptureFacets.Apply(list, new CaptureFilter(Text: "zdravo svet")));
    }

    [Fact]
    public async Task Text_prefix_search_matches_page_content()
    {
        await using var host = await AppHost.StartAsync();
        host.RouteSite("example.com/", SampleData.Captures(3));
        var text = await host.GetAsync("/Home/Text?ts=20200101000200&url=" + Uri.EscapeDataString("https://example.com/page2"));
        Assert.Contains("Page https://example.com/page2", text);
    }

    [Fact]
    public async Task Filtering_by_year_shows_only_matching_captures_and_keeps_the_total()
    {
        await using var host = await AppHost.StartAsync();
        var captures = SampleData.Captures(10);
        host.RouteSite("example.com/", captures);

        var all = await host.GetAsync("/?url=example.com/");
        Assert.Contains("class=\"timeline\"", all);

        var none = await host.GetAsync("/?url=example.com/&year=1999");
        Assert.Equal(0, none.Split("class=\"card capture-row\"").Length - 1);

        var matching = await host.GetAsync("/?url=example.com/&year=2020");
        Assert.Equal(10, matching.Split("class=\"card capture-row\"").Length - 1);
    }

    [Fact]
    public async Task History_endpoint_lists_captures_of_one_url_and_rejects_bad_input()
    {
        await using var host = await AppHost.StartAsync();
        host.RouteSite("example.com/", SampleData.Captures(3));
        await host.GetAsync("/?url=example.com/");

        var json = await host.GetAsync("/Home/History?url=example.com/&filter=html200&original=" +
            Uri.EscapeDataString("https://example.com/page1"));
        Assert.Contains("20200101000100", json);

        var bad = await host.Client.GetAsync("/Home/History?url=example.com/&original=javascript:alert(1)");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, bad.StatusCode);
    }
}
