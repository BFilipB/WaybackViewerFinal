using Microsoft.Extensions.DependencyInjection;
using WaybackViewer;
using Xunit;

namespace WaybackViewer.Tests;

public class TempDirectoryTests
{
    [Fact]
    public void RedirectTempDirectory_EmptyValue_LeavesTempUnchanged()
    {
        var before = Environment.GetEnvironmentVariable("TMP");
        Assert.Null(AppBuilder.RedirectTempDirectory(null));
        Assert.Null(AppBuilder.RedirectTempDirectory("  "));
        Assert.Equal(before, Environment.GetEnvironmentVariable("TMP"));
    }

    [Theory]
    [InlineData(@"Q:\no-such-drive\temp")] // the local E:\ setting seen from a machine without that drive
    [InlineData("Q:/no-such-drive/keys")]
    public void UsableDirectory_DriveMissing_IsSkippedSoStartupDoesNotFail(string dir)
    {
        if (OperatingSystem.IsWindows() && Directory.Exists("Q:\\")) return;
        var before = Environment.GetEnvironmentVariable("TMP");
        Assert.Null(AppBuilder.UsableDirectory(dir));
        Assert.Null(AppBuilder.RedirectTempDirectory(dir));
        Assert.Equal(before, Environment.GetEnvironmentVariable("TMP"));
    }

    [Fact]
    public void UsableDirectory_ExistingRootedPath_IsKept()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wv-usable");
        Assert.Equal(Path.GetFullPath(dir), AppBuilder.UsableDirectory(dir));
    }

    [Fact]
    public void RedirectTempDirectory_RootedPath_CreatesFolderAndPointsTempThere()
    {
        var oldTmp = Environment.GetEnvironmentVariable("TMP");
        var oldTemp = Environment.GetEnvironmentVariable("TEMP");
        var oldSqlite = Environment.GetEnvironmentVariable("SQLITE_TMPDIR");
        var target = Path.Combine(Path.GetTempPath(), "wv-temp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var applied = AppBuilder.RedirectTempDirectory(target);
            Assert.Equal(Path.GetFullPath(target), applied);
            Assert.True(Directory.Exists(target));
            Assert.Equal(applied, Environment.GetEnvironmentVariable("TMP"));
            Assert.Equal(applied, Environment.GetEnvironmentVariable("TEMP"));
            // .NET reads TMP/TEMP only on Windows (Linux/macOS use TMPDIR).
            if (OperatingSystem.IsWindows())
                Assert.StartsWith(applied!, Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMP", oldTmp);
            Environment.SetEnvironmentVariable("TEMP", oldTemp);
            Environment.SetEnvironmentVariable("SQLITE_TMPDIR", oldSqlite);
            if (Directory.Exists(target)) Directory.Delete(target, true);
        }
    }

    [Fact]
    public async Task KeysDirectory_Configured_PersistsDataProtectionKeysThere()
    {
        var keys = Path.Combine(Path.GetTempPath(), "wv-keys-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var app = AppBuilder.Build([], overrides: new Dictionary<string, string?>
            {
                ["Storage:Provider"] = "Memory",
                ["Storage:TempDirectory"] = "",
                ["Storage:KeysDirectory"] = keys,
                ["Logging:File:Directory"] = ""
            });
            var provider = app.Services.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>();
            provider.CreateProtector("test").Protect(new byte[] { 1 });
            Assert.NotEmpty(Directory.GetFiles(keys, "*.xml"));
        }
        finally
        {
            if (Directory.Exists(keys)) Directory.Delete(keys, true);
        }
    }
}