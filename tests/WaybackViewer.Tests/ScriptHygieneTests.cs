using System.Text.RegularExpressions;

namespace WaybackViewer.Tests;

public class ScriptHygieneTests
{
    // Catches "xBtn is not defined" script errors locally, without needing a browser.
    [Fact]
    public void Every_Btn_variable_used_in_the_page_script_is_declared()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "src", "WaybackViewer", "Views", "Home", "Index.cshtml")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var view = File.ReadAllText(Path.Combine(dir!, "src", "WaybackViewer", "Views", "Home", "Index.cshtml"));

        var used = Regex.Matches(view, @"(?<![\w'""#.-])([a-z][A-Za-z]*Btn)\b(?!['""])").Select(m => m.Groups[1].Value).Distinct();
        var missing = used.Where(name => !Regex.IsMatch(view, $@"(const|let|var)\s+[^;]*\b{name}\s*=") && !Regex.IsMatch(view, $@",\s*{name}\s*=")).ToList();
        Assert.True(missing.Count == 0, "Used but never declared: " + string.Join(", ", missing));
    }
}