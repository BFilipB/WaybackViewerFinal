using WaybackViewer.Parsing;

namespace WaybackViewer.Tests;

/// Multilingual / capitalized count words (real forums write "71 Views", "Visninger", "odgovora").
public class MultilingualCountTests
{
    private static (long? Views, long? Comments) Run(string body)
    {
        var html = "<body>" + body + "</body>";
        var parsed = PageParser.Parse(html);
        var m = PopularityEstimator.Enrich(parsed.Metadata, html, parsed.Text);
        return (m.Views, m.Comments);
    }

    [Theory]
    [InlineData("<dd>71 Views</dd>", 71, null)]
    [InlineData("<p>120 Visninger</p>", 120, null)]
    [InlineData("<p>340 Aufrufe</p>", 340, null)]
    [InlineData("<p>58 visitas</p>", 58, null)]
    [InlineData("<p>77 vues</p>", 77, null)]
    [InlineData("<p>90 pregleda</p>", 90, null)]
    [InlineData("<p>12 Kommentare</p>", null, 12)]
    [InlineData("<p>9 comentarios</p>", null, 9)]
    [InlineData("<p>4 commentaires</p>", null, 4)]
    [InlineData("<p>15 odgovora</p>", null, 15)]
    [InlineData("<p>6 Antworten</p>", null, 6)]
    public void Count_words_in_several_languages_are_read(string body, int? views, int? comments)
    {
        var (v, c) = Run(body);
        Assert.Equal((long?)views, v);
        Assert.Equal((long?)comments, c);
    }

    [Fact]
    public void Plain_words_that_merely_contain_a_count_word_are_not_counted()
    {
        var (v, c) = Run("<p>3 reviewers agreed</p>");
        Assert.Null(v);
        Assert.Null(c);
    }
}