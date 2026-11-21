using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace MapDropStatistics;

internal sealed class NinjaItemIcons(string root)
{
    private static readonly Uri GameCdn = new("https://web.poecdn.com/");
    private static readonly string[] ItemFiles =
    [
        "Invitations", "Vials", "Incubators", "Wombgifts", "Visceras", "WhiteMaps",
        "UniqueAccessories", "UniqueArmours", "UniqueFlasks", "UniqueJewels", "UniqueMaps", "UniqueWeapons"
    ];
    private readonly Dictionary<string, (DateTime Loaded, Task<Dictionary<string, string>> Icons)> _byLeague = new();

    public Task<Dictionary<string, string>> Get(string league)
    {
        league ??= string.Empty;
        lock (_byLeague)
        {
            var now = DateTime.UtcNow;
            if (_byLeague.TryGetValue(league, out var cached) && now - cached.Loaded < TimeSpan.FromMinutes(5))
                return cached.Icons;
            var icons = Task.Run(() => Read(league));
            _byLeague[league] = (now, icons);
            return icons;
        }
    }

    private Dictionary<string, string> Read(string league)
    {
        var icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root))
            return icons;

        try
        {
            var recent = new DirectoryInfo(root).EnumerateDirectories()
                .OrderByDescending(directory => File.GetLastWriteTimeUtc(Path.Combine(directory.FullName, "meta.json")))
                .Select(directory => directory.FullName).FirstOrDefault();
            var current = league.Length > 0 && league is not ("." or "..") && league.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
                ? Path.Combine(root, league) : null;
            foreach (var directory in new[] { current, recent, Path.Combine(root, "Standard") }
                         .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var name in NinjaPriceCache.ExchangeFiles.Concat(ItemFiles))
                    ReadFile(Path.Combine(directory, name + ".json"), icons);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Previously read icons still work while the pricer replaces its cache.
        }
        return icons;
    }

    private static void ReadFile(string path, Dictionary<string, string> icons)
    {
        try
        {
            if (!File.Exists(path))
                return;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return;
            if (root.TryGetProperty("core", out var core) && core.ValueKind == JsonValueKind.Object)
                ReadItems(core, "items", icons);
            ReadItems(root, "items", icons);
            ReadItems(root, "lines", icons);
            ReadItems(root, "Lines", icons);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An individual cache file may still be downloading.
        }
    }

    private static void ReadItems(JsonElement source, string key, Dictionary<string, string> icons)
    {
        if (!source.TryGetProperty(key, out var items) || items.ValueKind != JsonValueKind.Array)
            return;
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            var name = ReadString(item, "name") ?? ReadString(item, "Name");
            var image = ReadString(item, "image") ?? ReadString(item, "icon") ?? ReadString(item, "Icon");
            if (name != null && image != null && !icons.ContainsKey(name) && ResolveIcon(image) is { } url)
                icons.Add(name, url);
        }
    }

    private static string ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string ResolveIcon(string image)
    {
        if (image.StartsWith("/gen/image/", StringComparison.Ordinal) || image.StartsWith("/image/", StringComparison.Ordinal))
            return Uri.TryCreate(GameCdn, image, out var relative) && relative.Host == GameCdn.Host ? relative.AbsoluteUri : null;
        return Uri.TryCreate(image, UriKind.Absolute, out var absolute) && absolute.Scheme == Uri.UriSchemeHttps &&
               absolute.IsDefaultPort && absolute.UserInfo.Length == 0 && absolute.Host is "web.poecdn.com" or "poe.ninja"
            ? absolute.AbsoluteUri : null;
    }
}
