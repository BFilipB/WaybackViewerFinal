using WaybackViewer.Models;

namespace WaybackViewer.Tests;

public class SearchTextMatcherTests
{
    [Theory]
    [InlineData("hello world", "hello    world", true)]
    [InlineData("hello world", "hello-world", true)]
    [InlineData("hello world", "hello/world", true)]
    [InlineData("hello world", "hello brave world", true)]
    [InlineData("hello world", "hello", false)]
    public void Matches_tolerates_spacing_and_punctuation(string query, string value, bool expected)
        => Assert.Equal(expected, SearchTextMatcher.Matches(query, value));

    [Theory]
    [InlineData("cafe", "café")]
    [InlineData("CAFÉ", "cafe")]
    public void Matches_ignores_diacritics_and_case(string query, string value)
        => Assert.True(SearchTextMatcher.Matches(query, value));

    [Theory]
    [InlineData("zdravo", "здраво")]
    [InlineData("здраво", "zdravo")]
    [InlineData("njegos", "његош")]
    [InlineData("његош", "njegos")]
    public void Matches_supports_basic_latin_cyrillic_equivalence(string query, string value)
        => Assert.True(SearchTextMatcher.Matches(query, value));

    [Fact]
    public void Cache_key_is_stable_across_equivalent_forms()
    {
        var a = SearchTextMatcher.CacheKey("CAFÉ  zdravo");
        var b = SearchTextMatcher.CacheKey("cafe zdravo");
        Assert.Equal(a, b);
    }
}