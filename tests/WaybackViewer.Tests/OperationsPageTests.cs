using System.Text.RegularExpressions;
using WaybackViewer.Models;

namespace WaybackViewer.Tests;

/// Server-rendered Operations page: search/sort query parsing, links that keep state, and "select all matching" bulk actions.
public class OperationsPageTests
{
    private static async Task<string> GetAsync(AppHost host, string query) =>
        await host.Client.GetStringAsync("operations" + query);

    private static async Task SeedAsync(AppHost host, int count)
    {
        for (var i = 0; i < count; i++)
            await host.Store!.CreateOrGetOperationAsync($"site{i:00}.com/", "html200", OperationKind.CaptureLoad, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Pager_links_keep_search_and_sort_and_clear_drops_only_the_search()
    {
        await using var host = await AppHost.StartAsync();
        await SeedAsync(host, 12);

        var html = await GetAsync(host, "?q=SITE&sort=url&dir=asc&pageSize=10");

        Assert.Contains("site00.com/", html);                                   // url ascending, case-insensitive search
        Assert.DoesNotContain("site11.com/", html);                             // page 2
        Assert.Contains("q=SITE&amp;sort=url&amp;dir=asc", html);               // Next link keeps search + sort
        var clear = Regex.Match(html, "<a href=\"([^\"]*)\">Clear</a>");
        Assert.True(clear.Success);
        Assert.DoesNotContain("q=", clear.Groups[1].Value);
        Assert.Contains("sort=url", clear.Groups[1].Value);
    }

    [Fact]
    public async Task Invalid_sort_falls_back_and_long_search_is_capped()
    {
        await using var host = await AppHost.StartAsync();
        await SeedAsync(host, 1);
        var longText = new string('a', 300);

        var html = await GetAsync(host, $"?sort=bogus&dir=sideways&q={longText}");

        Assert.Matches("value=\"updated\" selected=\"selected\"", html);
        Assert.Matches("value=\"desc\" selected=\"selected\"", html);
        Assert.Contains($"value=\"{new string('a', OperationsControllerLimits.MaxSearch)}\"", html);
        Assert.DoesNotContain(new string('a', OperationsControllerLimits.MaxSearch + 1), html);
    }

    [Fact]
	public async Task Search_box_disables_browser_autofill_and_select_all_offers_every_page()
	{
		await using var host = await AppHost.StartAsync();
		await SeedAsync(host, 30);

		var html = await GetAsync(host, "?q=site&pageSize=10");

		Assert.Matches("<input[^>]*name=\"q\"[^>]*autocomplete=\"off\"", html);
		Assert.Contains("data-total=\"30\"", html);                           // the client offers "select all 30 matching"
		Assert.Contains("data-q=\"site\"", html);                             // and re-sends the same search
		Assert.DoesNotContain("data-all=\"true\"", html);                     // no hidden "all URLs" buttons any more
	}

	[Fact]
	public async Task Matching_selection_acts_on_every_page_of_the_search_only()
	{
		await using var host = await AppHost.StartAsync();
		var store = host.Store!;
		var ids = new List<long>();
		for (var i = 0; i < 25; i++)
		{
			var op = await store.CreateOrGetOperationAsync($"keep{i:00}.com/", "html200", OperationKind.CaptureLoad, DateTimeOffset.UtcNow);
			ids.Add(op.Id);
		}
		var other = await store.CreateOrGetOperationAsync("other.com/", "html200", OperationKind.CaptureLoad, DateTimeOffset.UtcNow);

		var response = await host.Client.PostAsync("/operations/pause",
			new StringContent(System.Text.Json.JsonSerializer.Serialize(new { matching = new { search = "KEEP" } }), System.Text.Encoding.UTF8, "application/json"));

		Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
		foreach (var id in ids) Assert.Equal(OperationState.Paused, (await store.GetOperationAsync(id))!.State);   // beyond the first page of 10 too
		Assert.Equal(OperationState.Queued, (await store.GetOperationAsync(other.Id))!.State);
	}

	private static StringContent Json(object value) =>
		new(System.Text.Json.JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json");

	[Fact]
	public async Task Matching_selection_reapplies_state_and_type_filters()
	{
		await using var host = await AppHost.StartAsync();
		var store = host.Store!;
		var load = await store.CreateOrGetOperationAsync("a.com/", "html200", OperationKind.CaptureLoad, DateTimeOffset.UtcNow);
		var preload = await store.CreateOrGetOperationAsync("b.com/", "html200", OperationKind.PagePreload, DateTimeOffset.UtcNow);

		var response = await host.Client.PostAsync("/operations/pause", Json(new { matching = new { type = "preload" } }));

		Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
		Assert.Equal(OperationState.Paused, (await store.GetOperationAsync(preload.Id))!.State);
		Assert.Equal(OperationState.Queued, (await store.GetOperationAsync(load.Id))!.State);   // other type untouched
	}

	[Theory]
	[InlineData("bogus", null)]
	[InlineData(null, "bogus")]
	public async Task Matching_selection_with_unknown_filter_is_rejected_and_changes_nothing(string? state, string? type)
	{
		await using var host = await AppHost.StartAsync();
		var op = await host.Store!.CreateOrGetOperationAsync("a.com/", "html200", OperationKind.CaptureLoad, DateTimeOffset.UtcNow);

		var response = await host.Client.PostAsync("/operations/pause", Json(new { matching = new { state, type } }));

		Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Equal(OperationState.Queued, (await host.Store.GetOperationAsync(op.Id))!.State);
	}

	[Fact]
	public async Task Page_script_keeps_selection_across_pages_per_scope()
	{
		await using var host = await AppHost.StartAsync();
		await SeedAsync(host, 1);

		var html = await GetAsync(host, "");

		Assert.Contains("sessionStorage", html);          // selection survives paging
		Assert.Contains("'opsSel:'", html);               // keyed by search/filter scope
		Assert.Contains("matching: scope()", html);       // "all matching" re-sends the scope
	}

    private static class OperationsControllerLimits
    {
        public const int MaxSearch = WaybackViewer.Controllers.OperationsController.MaxSearchLength;
    }
}
