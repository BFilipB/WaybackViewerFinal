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
    public void Pagination_defaults_to_client_mode()
    {
        var model = new CaptureListViewModel("example.com", "html200", [], null);

        Assert.Equal("client", model.PaginationMode);
        Assert.Equal(50, model.PageSize);
    }
}
