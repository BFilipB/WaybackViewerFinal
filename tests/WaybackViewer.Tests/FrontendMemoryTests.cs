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

    [Fact]
    public void Filtered_export_streams_from_the_server_and_slideshow_list_is_capped()
    {
        var home = View("Home", "Index.cshtml");
        Assert.Contains("a.href = '/Home/Export?'", home);
        Assert.DoesNotContain("await exportText(all)", home);
        Assert.Contains("params.set('max', String(SHOW_MAX))", home);
    }

    [Fact]
    public void Layout_has_no_prefetch_or_view_transitions_and_defaults_to_subtle_motion()
    {
        var layout = View("Shared", "_Layout.cshtml");
        Assert.DoesNotContain("speculationrules\">", layout);
        Assert.DoesNotContain("@view-transition", layout);
        Assert.DoesNotContain("|| 'rich'", layout);
        Assert.Contains("|| 'subtle'", layout);
        Assert.Contains("}, 30000);", layout);   // header clock: not once a second
    }

    [Fact]
    public void Slideshow_defaults_to_text_loads_one_archived_page_and_releases_frames_when_hidden_or_slow()
    {
        var home = View("Home", "Index.cshtml");
        Assert.Contains("const AHEAD = 0,", home);
        Assert.Contains("localStorage.getItem('wb-mode') === 'full' ? 'full' : 'fast'", home);
        Assert.Contains("if (document.hidden) dropFrames(); else showSlide();", home);
        Assert.Contains("mode = 'fast';", home);
        var timeout = home.IndexOf("mode = 'fast';", StringComparison.Ordinal);
        Assert.Contains("dropFrames();", home.Substring(timeout - 200, 200));
    }

    [Fact]
    public void Archived_page_scripts_are_off_unless_the_user_opts_in()
    {
        var home = View("Home", "Index.cshtml");
        Assert.Contains("scripts ? 'allow-scripts allow-popups' : 'allow-popups'", home);
        Assert.Contains("wb-scripts", home);
        Assert.Contains("'&scripts=true'", home);      // binds to the Frame action's bool parameter
        Assert.Contains("id=\"ssScripts\"", home);
        Assert.DoesNotContain("`r`n", home);              // no escaped newlines leaked into markup
    }
}