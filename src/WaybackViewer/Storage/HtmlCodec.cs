using System.IO.Compression;
using System.Text;

namespace WaybackViewer.Storage;

/// Pages are stored gzip-compressed (typically 5-10x smaller) as UTF-8.
public static class HtmlCodec
{
    public static byte[] Compress(string html)
    {
        var raw = Encoding.UTF8.GetBytes(html);
        using var output = new MemoryStream(raw.Length / 3 + 64);
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(raw, 0, raw.Length);
        return output.ToArray();
    }

    public static string Decompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
