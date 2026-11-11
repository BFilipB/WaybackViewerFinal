using Xunit;

namespace WaybackViewer.Tests;

/// Guards the browser-side memory limits: hidden tabs stop work, live events are limited, frames are released.
public class FrontendMemoryTests
{
    private static string View(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "WaybackViewer"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine([dir!, "src", "WaybackViewer", "Views", .. parts]));
    }

    [Fact]
    public void Live_stream_is_closed_while_the_tab_is_hidden()
    {
        var layout = View("Shared", "_Layout.cshtml");
        Assert.Contains("if (document.hidden) closeLive(); else openLive();", layout);
        Assert.Contains("window.addEventListener('pagehide', closeLive);", layout);
    }

    [Fact]
    public void Operations_page_limits_live_refreshes_and_skips_hidden_tabs()
    {
        var ops = View("Operations", "Index.cshtml");
        Assert.Contains("}, 2000); });", ops);
        Assert.Contains("document.hidden || (e.detail && !shown(e.detail))", ops);
        Assert.Contains("if (document.hidden) { setTimeout(poll, 5000); return; }", ops);
        Assert.Contains("el.textContent !== s", ops);
    }

    [Fact]
    public void Slideshow_releases_frames_and_keeps_a_small_text_cache()
    {
        var home = View("Home", "Index.cshtml");
        Assert.Contains("f.el.src = 'about:blank'; f.el.remove();", home);
        Assert.Contains("TEXT_CACHE_MAX = 24", home);
        Assert.Contains("if (document.hidden) { wfLater(10000); return; }", home);
    }
}
