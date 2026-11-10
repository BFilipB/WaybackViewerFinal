using Microsoft.Extensions.Logging;
using WaybackViewer.Services;
using Xunit;

namespace WaybackViewer.Tests;

public class FileLoggerTests
{
    [Fact]
    public void Writes_formatted_lines_and_flushes_on_dispose()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wbv-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var provider = new FileLoggerProvider(dir))
            {
                var log = provider.CreateLogger("Test.Category");
                log.LogInformation("hello {X}", 42);
                log.LogDebug("hidden");
                log.LogError(new InvalidOperationException("boom"), "failed");
            }
            var text = File.ReadAllText(Directory.GetFiles(dir, "wayback-*.log").Single());
            Assert.Contains($" INF [{RunInfo.Id}] Test.Category hello 42", text);
            Assert.Contains($" ERR [{RunInfo.Id}] Test.Category failed | System.InvalidOperationException: boom", text);
            Assert.DoesNotContain("hidden", text);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
