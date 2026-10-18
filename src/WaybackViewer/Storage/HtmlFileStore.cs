using System.Security.Cryptography;
using System.Text;
using WaybackViewer.Models;

namespace WaybackViewer.Storage;

/// File-backed payload store for compressed HTML, organized by host/year/month/day.
public sealed class HtmlFileStore
{
    private readonly string root;

    public HtmlFileStore(string root)
    {
        this.root = root;
        Directory.CreateDirectory(root);
    }

    public string Root => root;

    public string PathFor(Capture capture)
    {
        var host = HostOf(capture.Original);
        var (year, month, day) = DateParts(capture.Timestamp);
        var file = SafeFileName(capture) + ".gz";
        return System.IO.Path.Combine(root, host, year, month, day, file);
    }

    public async Task SaveAsync(Capture capture, byte[] htmlGz, CancellationToken ct = default)
    {
        var path = PathFor(capture);
        var dir = System.IO.Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        // Same capture can be reached by concurrent workflows/tests; allow cooperative overlap and retry briefly.
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(path, new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    Share = FileShare.ReadWrite | FileShare.Delete,
                    Options = FileOptions.Asynchronous,
                    BufferSize = 1
                });
                await stream.WriteAsync(htmlGz, ct).ConfigureAwait(false);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(15 * (attempt + 1), ct).ConfigureAwait(false);
            }
        }
    }

    public async Task<byte[]?> LoadAsync(Capture capture, CancellationToken ct = default)
    {
        var path = PathFor(capture);
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous,
            BufferSize = 4096
        });
        var bytes = new byte[stream.Length];
        var read = 0;
        while (read < bytes.Length)
        {
            var n = await stream.ReadAsync(bytes.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) break;
            read += n;
        }
        if (read == bytes.Length) return bytes;
        return bytes.AsSpan(0, read).ToArray();
    }

    public bool Exists(Capture capture) => File.Exists(PathFor(capture));

    private static string HostOf(string original)
    {
        if (!Uri.TryCreate(original, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            return "unknown";
        return string.Concat(uri.Host.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '.' ? ch : '_'));
    }

    private static (string Year, string Month, string Day) DateParts(string timestamp)
    {
        var year = timestamp.Length >= 4 ? timestamp[..4] : "0000";
        var month = timestamp.Length >= 6 ? timestamp[4..6] : "00";
        var day = timestamp.Length >= 8 ? timestamp[6..8] : "00";
        return (year, month, day);
    }

    private static string SafeFileName(Capture capture)
    {
        var input = capture.Timestamp + "|" + capture.Original;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
