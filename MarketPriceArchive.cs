using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace MapDropStatistics;

internal sealed record MarketPriceSnapshot(DateTime CapturedUtc, NinjaPriceCache Quotes);

internal sealed class MarketPriceArchive(string directory, Func<string> getCacheRoot, Action<string> logError)
{
    public const int WindowHours = 3;
    public const int LegacyWindowHours = 12;
    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTime WindowUtc, MarketPriceSnapshot Snapshot)> _lastByLeague =
        new(StringComparer.OrdinalIgnoreCase);

    public string DirectoryPath { get; } = Path.GetFullPath(directory);

    public static DateTime WindowStart(DateTime utc, int hours = WindowHours) => utc.Date.AddHours(utc.Hour / hours * hours);

    public static bool Covers(MarketPriceSnapshot snapshot, DateTime startedUtc) =>
        WindowStart(snapshot.CapturedUtc) == WindowStart(startedUtc);

    public MarketPriceSnapshot Get(string league, DateTime mapStartedUtc, DateTime nowUtc, int hours = WindowHours)
    {
        if (!NinjaPriceCache.IsValidLeague(league) || hours is not (WindowHours or LegacyWindowHours))
            return null;

        var window = WindowStart(mapStartedUtc, hours);
        var currentFormat = hours == WindowHours;
        lock (_gate)
        {
            if (currentFormat && _lastByLeague.TryGetValue(league, out var cached) && cached.WindowUtc == window)
                return cached.Snapshot;

            try
            {
                var stamp = window.ToString("yyyy-MM-dd-HH", CultureInfo.InvariantCulture);
                var suffix = currentFormat ? "-3h.json.gz" : ".json.gz";
                var path = Path.Combine(DirectoryPath, league, stamp + suffix);
                MarketPriceSnapshot snapshot;
                if (File.Exists(path))
                {
                    snapshot = Read(path);
                }
                else
                {
                    if (!currentFormat || window != WindowStart(nowUtc))
                        return null;
                    var root = getCacheRoot();
                    var metaPath = Path.Combine(root, league, "meta.json");
                    var currencyPath = Path.Combine(root, league, "Currency2.json");
                    // The pricer writes its metadata after refreshing the category files.
                    var metaBefore = File.GetLastWriteTimeUtc(metaPath);
                    var currencyBefore = File.GetLastWriteTimeUtc(currencyPath);
                    var quotes = NinjaPriceCache.Load(root, league);
                    if (quotes.Find("Chaos Orb") is not > 0 || metaBefore != File.GetLastWriteTimeUtc(metaPath) ||
                        currencyBefore != File.GetLastWriteTimeUtc(currencyPath) ||
                        (File.Exists(metaPath) && currencyBefore > metaBefore))
                        return null;

                    snapshot = SaveNew(path, new MarketPriceSnapshot(nowUtc, quotes));
                }

                if (currentFormat)
                    _lastByLeague[league] = (window, snapshot);
                return snapshot;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                logError($"Could not read or save market prices for {league}: {ex.Message}");
                return null;
            }
        }
    }

    private static MarketPriceSnapshot Read(string path)
    {
        using var file = File.OpenRead(path);
        using var stream = new GZipStream(file, CompressionMode.Decompress);
        var snapshot = JsonSerializer.Deserialize<MarketPriceSnapshot>(stream, MapHistory.Json);
        return snapshot?.Quotes?.Find("Chaos Orb") is > 0
            ? snapshot : throw new JsonException("Market price snapshot is missing a Chaos Orb quote.");
    }

    private static MarketPriceSnapshot SaveNew(string path, MarketPriceSnapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = File.Create(temporary))
            using (var stream = new GZipStream(file, CompressionLevel.Fastest))
                JsonSerializer.Serialize(stream, snapshot, MapHistory.Json);
            try
            {
                File.Move(temporary, path);
                return snapshot;
            }
            catch (IOException) when (File.Exists(path))
            {
                return Read(path);
            }
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
