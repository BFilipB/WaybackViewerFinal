using System.Net.Http.Headers;
using System.Text;
using WaybackViewer.Services;

namespace WaybackViewer.Tests;

public class ArchiveBodyDecodingTests
{
    private static ByteArrayContent Content(byte[] bytes, string? charset)
    {
        var c = new ByteArrayContent(bytes);
        c.Headers.ContentType = new MediaTypeHeaderValue("text/html") { CharSet = charset };
        return c;
    }

    [Fact]
    public async Task Windows_1251_page_is_decoded_with_its_code_page()
    {
        var text = "Привет мир";
        var bytes = CodePagesEncodingProvider.Instance.GetEncoding(1251)!.GetBytes(text);
        Assert.Equal(text, await ArchiveClient.ReadBodyAsync(Content(bytes, "windows-1251"), CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-such-charset")]
    public async Task Missing_or_unknown_charset_falls_back_to_utf8(string? charset)
    {
        var bytes = Encoding.UTF8.GetBytes("héllo");
        Assert.Equal("héllo", await ArchiveClient.ReadBodyAsync(Content(bytes, charset), CancellationToken.None));
    }

    [Fact]
    public void Resolve_encoding_handles_quotes_and_case()
    {
        Assert.Equal(1251, ArchiveClient.ResolveEncoding("\"Windows-1251\"").CodePage);
        Assert.Equal(Encoding.UTF8.CodePage, ArchiveClient.ResolveEncoding("bogus").CodePage);
    }

    [Fact]
    public async Task Large_body_is_read_completely()
    {
        var text = new string('a', 3_000_000);
        Assert.Equal(text.Length, (await ArchiveClient.ReadBodyAsync(Content(Encoding.UTF8.GetBytes(text), "utf-8"), CancellationToken.None)).Length);
    }
}
