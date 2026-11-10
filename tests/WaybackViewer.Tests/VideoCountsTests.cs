using WaybackViewer.Parsing;

namespace WaybackViewer.Tests;

public class VideoCountsTests
{
    private const string Player =
        "<html><head><meta itemprop=\"interactionCount\" content=\"9500\"><meta itemprop=\"uploadDate\" content=\"2026-05-01T10:00:15-07:00\"></head>" +
        "<body><script>var ytInitialPlayerResponse = {\"videoDetails\":{\"viewCount\":\"9500\",\"likeCount\":\"1492\"}," +
        "\"later\":{\"viewCount\":\"71157\",\"likeCount\":\"5\"}};</script></body></html>";

    [Fact]
    public void Embedded_player_json_gives_the_pages_own_views_and_likes_not_the_recommendations()
    {
        var m = PageParser.ExtractMetadata(Player);
        Assert.Equal(9500, m?.Views);
        Assert.Equal(1492, m?.Likes);
        Assert.Equal(2026, m?.PublishedAt?.Year);
    }

    [Fact]
    public void Microdata_interaction_count_and_upload_date_are_read_without_embedded_json()
    {
        var m = PageParser.ExtractMetadata("<html><head><meta itemprop=\"interactionCount\" content=\"123\"><meta itemprop=\"uploadDate\" content=\"2019-03-04\"></head></html>");
        Assert.Equal(123, m?.Views);
        Assert.Equal(2019, m?.PublishedAt?.Year);
    }

    [Fact]
    public void Json_ld_video_object_gives_upload_date_and_watch_count()
    {
        var m = PageParser.ExtractMetadata("<html><script type=\"application/ld+json\">{\"@type\":\"VideoObject\",\"uploadDate\":\"2018-01-02T03:04:05Z\"," +
            "\"interactionStatistic\":{\"interactionType\":{\"@type\":\"WatchAction\"},\"userInteractionCount\":777}}</script></html>");
        Assert.Equal(777, m?.Views);
        Assert.Equal(2018, m?.PublishedAt?.Year);
    }

    [Fact]
    public void Pages_without_embedded_player_json_are_left_alone()
    {
        var m = PageParser.ExtractMetadata("<html><head><title>t</title></head><body>\"viewCount\":\"5\"</body></html>");
        Assert.Null(m?.Views);
    }
}
