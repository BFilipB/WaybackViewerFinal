using WaybackViewer;
using WaybackViewer.Models;
using WaybackViewer.Parsing;
using WaybackViewer.Storage;

namespace WaybackViewer.Tests;

public class UrlInputTests
{
    [Theory]
    [InlineData("https://example.com/blog/*", "example.com/blog/")]
    [InlineData("  HTTP://Example.COM/Blog/  ", "example.com/blog/")]
    [InlineData("example.com", "example.com")]
    [InlineData("https://web.archive.org/web/*/https://example.com/news/*", "example.com/news/")]
    [InlineData("http://web.archive.org/web/20200101000000*/https://example.com/a/*", "example.com/a/")]
    [InlineData("example.com/path/**", "example.com/path/")]
    public void NormalizePrefix_accepts_common_inputs(string input, string expected) =>
        Assert.Equal(expected, UrlInput.NormalizePrefix(input));

    [Theory]
    [InlineData((string?)null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("two words")]
    [InlineData("https://")]
    [InlineData("*")]
    public void NormalizePrefix_rejects_unusable_input(string? input) =>
        Assert.Null(UrlInput.NormalizePrefix(input));

    [Fact]
    public void NormalizePrefix_rejects_overlong_input() =>
        Assert.Null(UrlInput.NormalizePrefix("example.com/" + new string('a', 500)));

    [Theory]
    [InlineData("html", "html")]
    [InlineData("all", "all")]
    [InlineData("html200", "html200")]
    [InlineData("nonsense", "html200")]
    [InlineData(null, "html200")]
    public void NormalizeFilter_falls_back_to_default(string? input, string expected) =>
        Assert.Equal(expected, UrlInput.NormalizeFilter(input));

    [Theory]
    [InlineData("20200101120000", true)]
    [InlineData("2020010112000", false)]
    [InlineData("2020010112000a", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidTimestamp_requires_14_digits(string? ts, bool expected) =>
        Assert.Equal(expected, UrlInput.IsValidTimestamp(ts));

    [Theory]
    [InlineData("https://example.com/a?b=c", true)]
    [InlineData("http://example.com", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("/relative/path", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidCaptureUrl_only_allows_http_urls(string? url, bool expected) =>
        Assert.Equal(expected, UrlInput.IsValidCaptureUrl(url));
}

public class PageParserTests
{
    [Fact]
    public void Extracts_title_description_and_visible_text()
    {
        var page = PageParser.Parse(
            "<html><head><title>My Page</title><meta name=\"description\" content=\"About it\">" +
            "<script>var x = 1;</script><style>p{color:red}</style></head>" +
            "<body><h1>Hello</h1><p>World &amp; friends</p><script>alert('x')</script></body></html>");

        Assert.Equal("My Page", page.Title);
        Assert.Equal("About it", page.Description);
        Assert.Equal("Hello\nWorld & friends", page.Text);
    }

    [Fact]
    public void Open_graph_values_win_over_plain_tags()
    {
        var page = PageParser.Parse(
            "<head><title>Plain</title><meta name='description' content='plain d'>" +
            "<meta property=\"og:title\" content=\"OG title\"><meta property=\"og:description\" content=\"OG desc\"></head><body>x</body>");

        Assert.Equal("OG title", page.Title);
        Assert.Equal("OG desc", page.Description);
    }

    [Fact]
    public void Meta_attributes_may_come_in_any_order_and_be_unquoted()
    {
        var page = PageParser.Parse("<meta content=Hello name=description><body>x</body>");
        Assert.Equal("Hello", page.Description);
    }

    [Fact]
    public void Header_element_is_not_treated_as_head()
    {
        var page = PageParser.Parse("<body><header>Site name</header><p>Content</p></body>");
        Assert.Contains("Site name", page.Text);
        Assert.Contains("Content", page.Text);
    }

    [Fact]
    public void Comments_doctype_and_noscript_are_ignored()
    {
        var page = PageParser.Parse("<!DOCTYPE html><body><!-- secret -->Visible<noscript>enable js</noscript></body>");
        Assert.Equal("Visible", page.Text);
    }

    [Fact]
    public void Literal_less_than_signs_in_text_survive()
    {
        var page = PageParser.Parse("<p>if a < b then c</p>");
        Assert.Equal("if a < b then c", page.Text);
    }

    [Fact]
    public void Whitespace_is_collapsed_and_blocks_become_lines()
    {
        var page = PageParser.Parse("<div>one   \n\n  two</div><div>three<br>four</div><table><tr><td>a</td><td>b</td></tr></table>");
        Assert.Equal("one\ntwo\nthree\nfour\na b", page.Text); // line breaks in the source are kept
    }

    [Fact]
    public void Truncated_html_does_not_throw()
    {
        var page = PageParser.Parse("<html><body><p>Hello</p><div class=\"x");
        Assert.Contains("Hello", page.Text);
    }

    [Fact]
    public void Empty_input_gives_empty_page()
    {
        var page = PageParser.Parse("");
        Assert.Null(page.Title);
        Assert.Null(page.Description);
        Assert.Equal("", page.Text);
    }

    [Fact]
    public void Text_and_title_are_length_limited()
    {
        var page = PageParser.Parse("<title>" + new string('t', 5000) + "</title><body>" + new string('x', 500_000) + "</body>");
        Assert.Equal(PageParser.MaxTitleChars, page.Title!.Length);
        Assert.True(page.Text.Length <= PageParser.MaxTextChars);
    }

    [Fact]
    public void Large_document_is_parsed_quickly()
    {
        var html = "<html><body>" + string.Concat(Enumerable.Repeat("<div><p>Some <b>bold</b> text &amp; more</p></div>", 60_000)) + "</body></html>";
        var started = DateTime.UtcNow;
        var page = PageParser.Parse(html);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3), "parsing took too long");
        Assert.StartsWith("Some bold text & more", page.Text);
    }
}

public class FrameHtmlTests
{
    [Fact]
    public void Base_tag_goes_right_after_head_open()
    {
        var html = FrameHtml.InjectBase("<html><head><title>x</title></head><body></body></html>", "https://web.archive.org/web/1if_/https://a.com/");
        Assert.Contains("<head><base href=\"https://web.archive.org/web/1if_/https://a.com/\"><title>", html);
    }

    [Fact]
    public void Head_with_attributes_is_supported()
    {
        var html = FrameHtml.InjectBase("<HEAD profile=\"x\"><title>t</title></HEAD>", "https://b/");
        Assert.StartsWith("<HEAD profile=\"x\"><base href=\"https://b/\">", html);
    }

    [Fact]
    public void Header_element_is_not_mistaken_for_head()
    {
        var html = FrameHtml.InjectBase("<html><body><header>Hi</header></body></html>", "https://b/");
        Assert.Contains("<html><head><base href=\"https://b/\"></head><body><header>Hi</header>", html);
    }

    [Fact]
    public void Fragment_without_head_or_html_gets_prefix() =>
        Assert.Equal("<base href=\"https://b/\"><p>x</p>", FrameHtml.InjectBase("<p>x</p>", "https://b/"));

    [Fact]
    public void Base_url_is_html_encoded() =>
        Assert.Contains("href=\"https://b/?a=1&amp;b=&quot;2&quot;\"", FrameHtml.InjectBase("<head></head>", "https://b/?a=1&b=\"2\""));

    [Fact]
    public void Binary_content_is_detected()
    {
        Assert.True(FrameHtml.LooksBinary("\u001f\uFFFD\b\0\0\0\0\0\0\0<\uFFFD\uFFFD"));
        Assert.False(FrameHtml.LooksBinary("<html><body>Hello\tworld\r\n</body></html>"));
        Assert.False(FrameHtml.LooksBinary(""));
    }
}

public class CdxParserTests
{
    [Fact]
    public void Skips_header_and_parses_rows()
    {
        var list = CdxParser.Parse("[[\"timestamp\",\"original\",\"mimetype\",\"statuscode\"],[\"20200101000000\",\"https://a.com/\",\"text/html\",\"200\"],[\"20210101000000\",\"https://a.com/b\",\"text/html\",\"301\"]]");
        Assert.Equal(2, list.Count);
        Assert.Equal(new Capture("20200101000000", "https://a.com/", "text/html", "200"), list[0]);
        Assert.Equal("301", list[1].StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[]")]
    public void Empty_responses_give_empty_list(string json) => Assert.Empty(CdxParser.Parse(json));

    [Fact]
    public void Malformed_rows_are_skipped() =>
        Assert.Single(CdxParser.Parse("[[\"timestamp\",\"original\",\"mimetype\",\"statuscode\"],[\"1\"],[\"20200101000000\",\"u\",\"m\",\"200\"]]"));

    [Fact]
    public void Query_url_depends_on_filter()
    {
        var html200 = ArchiveUrls.Cdx("example.com/a/", "html200");
        var html = ArchiveUrls.Cdx("example.com/a/", "html");
        var all = ArchiveUrls.Cdx("example.com/a/", "all");

        Assert.Contains("url=example.com%2Fa%2F", html200);
        Assert.Contains("matchType=prefix", html200);
        Assert.Contains("collapse=urlkey", html200);
        Assert.Contains("filter=mimetype:text/html", html200);
        Assert.Contains("filter=statuscode:200", html200);
        Assert.Contains("filter=mimetype:text/html", html);
        Assert.DoesNotContain("statuscode:200", html);
        Assert.DoesNotContain("filter=", all);
        Assert.DoesNotContain("limit=", html200); // never capped
    }
}

public class CaptureAndCodecTests
{
    [Fact]
    public void Capture_builds_archive_links_and_date()
    {
        var c = new Capture("20200102030405", "https://a.com/x", "text/html", "200");
        Assert.Equal("https://web.archive.org/web/20200102030405/https://a.com/x", c.ArchiveUrl);
        Assert.Equal("https://web.archive.org/web/20200102030405if_/https://a.com/x", c.FrameUrl);
        Assert.Equal("https://web.archive.org/web/20200102030405id_/https://a.com/x", ArchiveUrls.Raw(c.Timestamp, c.Original));
        Assert.Equal(new DateTime(2020, 1, 2, 3, 4, 5), c.Date);
        Assert.Equal("20200102030405|https://a.com/x", c.Key);
        Assert.True(c.IsHtml);
        Assert.False(new Capture("1", "u", "image/png", "200").IsHtml);
    }

    [Fact]
    public void Invalid_timestamp_gives_min_date() =>
        Assert.Equal(DateTime.MinValue, new Capture("garbage", "u", "text/html", "200").Date);

    [Fact]
    public void Html_codec_round_trips_unicode()
    {
        var html = "<p>Zdravo svete - Здраво свету - 你好 - 😀</p>";
        Assert.Equal(html, HtmlCodec.Decompress(HtmlCodec.Compress(html)));
    }

    [Fact]
    public void Html_codec_shrinks_repetitive_pages()
    {
        var html = string.Concat(Enumerable.Repeat("<div class=\"row\">same markup</div>", 5000));
        Assert.True(HtmlCodec.Compress(html).Length < html.Length / 10);
    }
}
