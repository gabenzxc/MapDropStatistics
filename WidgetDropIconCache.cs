using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace MapDropStatistics;

internal sealed record WidgetDropIcon(string FilePath, int Width, int Height);

internal sealed class WidgetDropIconCache(string directory, NinjaItemIcons catalog)
{
    private static readonly byte[] PngHeader = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(4),
        MaxResponseContentBufferSize = 1_048_576
    };

    public Task<WidgetDropIcon> Get(string name, string artPath, string league) => Task.Run(async () =>
    {
        if (GetArtUrl(artPath) is { } artUrl && await Load(artUrl).ConfigureAwait(false) is { } art)
            return art;
        var icons = await catalog.Get(league).ConfigureAwait(false);
        return icons.TryGetValue(name, out var iconUrl) ? await Load(iconUrl).ConfigureAwait(false) : null;
    });

    internal static string GetArtUrl(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        path = path.Replace('\\', '/');
        if (!path.StartsWith("Art/2DItems/", StringComparison.OrdinalIgnoreCase) ||
            !path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            return null;
        var segments = (path[..^4] + ".png").Split('/');
        if (segments.Any(segment => segment is "" or "." or ".."))
            return null;
        return "https://web.poecdn.com/image/" + string.Join('/', segments.Select(Uri.EscapeDataString)) + "?scale=1";
    }

    private async Task<WidgetDropIcon> Load(string url)
    {
        try
        {
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
            var path = Path.Combine(directory, key + ".png");
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                var header = new byte[24];
                if (stream.ReadAtLeast(header, header.Length, false) == header.Length &&
                    ReadPngSize(path, header) is { } cached)
                    return cached;
            }

            var bytes = await Client.GetByteArrayAsync(url).ConfigureAwait(false);
            var icon = ReadPngSize(path, bytes);
            if (icon == null)
                return null;
            Directory.CreateDirectory(directory);
            var temp = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                File.Delete(temp);
            }
            return icon;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    private static WidgetDropIcon ReadPngSize(string path, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 24 || !bytes[..8].SequenceEqual(PngHeader) || !bytes[12..16].SequenceEqual("IHDR"u8))
            return null;
        var width = BinaryPrimitives.ReadInt32BigEndian(bytes[16..]);
        var height = BinaryPrimitives.ReadInt32BigEndian(bytes[20..]);
        return width is > 0 and <= 1024 && height is > 0 and <= 1024 ? new(path, width, height) : null;
    }
}
