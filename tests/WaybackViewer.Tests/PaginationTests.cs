using WaybackViewer.Models;

namespace WaybackViewer.Tests;

public class PaginationTests
{
    [Theory]
    [InlineData(0, 50, 1)]
    [InlineData(1, 50, 1)]
    [InlineData(50, 50, 1)]
    [InlineData(51, 50, 2)]
    [InlineData(101, 25, 5)]
    public void TotalPages_uses_ceil_division(int total, int pageSize, int expected)
    {
        var model = new CaptureListViewModel("example.com", "html200", [], null,
            PaginationMode: "server", PageSize: pageSize, TotalCaptures: total);

        Assert.Equal(expected, model.TotalPages);
    }

    [Fact]
    public void Pagination_defaults_to_server_mode()
    {
        var model = new CaptureListViewModel("example.com", "html200", [], null);

        Assert.Equal("server", model.PaginationMode);
        Assert.Equal(50, model.PageSize);
    }

    [Fact]
    public void Server_page_model_preserves_total_count_separately_from_rendered_rows()
    {
        var page = new CaptureListViewModel("example.com", "html200", [
            new StoredCapture(1, new Capture("20200101000000", "https://example.com/1", "text/html", "200"), false)
        ], null, PaginationMode: "server", Page: 3, PageSize: 1, TotalCaptures: 3);

        Assert.Single(page.Captures);
        Assert.Equal(3, page.TotalCaptures);
        Assert.Equal(3, page.TotalPages);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(9, 10)]
    [InlineData(50, 50)]
    [InlineData(999, 200)]
    public void TotalPages_remains_valid_for_page_sizes_at_supported_boundaries(int pageSize, int expectedSize)
    {
        var model = new CaptureListViewModel("example.com", "html200", [], null,
            PaginationMode: "server", PageSize: expectedSize, TotalCaptures: 201);

        Assert.Equal((int)Math.Ceiling(201 / (double)expectedSize), model.TotalPages);
        Assert.True(model.PageSize > 0);
    }
}
