using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MapDropStatistics;

internal sealed class NinjaPriceCache
{
    internal const string NightmareMap = "Nightmare Map";
    internal static readonly string[] ExchangeFiles =
    [
        "Currency2", "Fragments2", "Scarabs2", "DivinationCards2", "Essences2", "Fossils2", "Resonators2",
        "Oils2", "DeliriumOrbs2", "Artifacts2", "Tattoos2", "Omens2", "KalguuranRunes2", "AllflameEmbers2",
        "DjinnCoinsUrl2", "Astrolabe2", "Ducats", "Threads"
    ];

    private readonly Dictionary<string, double> _prices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _uniquePrices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<(int? Tier, double Divine)>> _mapPrices = new(StringComparer.OrdinalIgnoreCase);

    public static NinjaPriceCache Load(string root, string league)
    {
        var prices = new NinjaPriceCache();
        if (string.IsNullOrWhiteSpace(league) || league is "." or ".." || league.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return prices;

        var directory = Path.Combine(root, league);
        foreach (var name in ExchangeFiles)
            ReadFile(Path.Combine(directory, name + ".json"), prices.ReadExchange);

        foreach (var name in new[] { "Invitations", "Vials", "Incubators", "Wombgifts", "Visceras", "ValdoMaps" })
            ReadFile(Path.Combine(directory, name + ".json"), prices.ReadFlatItemPrices);
        ReadFile(Path.Combine(directory, "WhiteMaps.json"), prices.ReadMapPrices);
        foreach (var name in new[] { "UniqueAccessories", "UniqueArmours", "UniqueFlasks", "UniqueJewels", "UniqueMaps", "UniqueWeapons" })
            ReadFile(Path.Combine(directory, name + ".json"), prices.ReadUniquePrices);
        return prices;
    }

    public static Dictionary<string, double> LoadThreadQuotes(string root, string league)
    {
        var quotes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(league) || league is "." or ".." ||
            league.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return quotes;

        var prices = new NinjaPriceCache();
        ReadFile(Path.Combine(root, league, "Threads.json"), prices.ReadExchange);
        foreach (var name in MapThreadUsage.NamesByStat.Values)
        {
            if (prices.Find(name) is { } price)
                quotes[name] = price;
        }
        return quotes;
    }

    public double? FindLoot(MapLoot item)
    {
        if (item.Category == "Unique")
            return _uniquePrices.TryGetValue(item.Name, out var unique) ? unique : null;
        if (item.Category == "Map" || item.Name == NightmareMap)
            return FindMap(item.Name, item.MapTier);
        return item.Category is "Magic" or "Rare" ? null : Find(item.Name);
    }

    private void ReadUniquePrices(JsonElement root)
    {
        if (!root.TryGetProperty("lines", out var lines) && !root.TryGetProperty("Lines", out lines))
            return;
        foreach (var entry in lines.EnumerateArray())
        {
            var name = Text(entry, "name", "Name");
            var variant = Text(entry, "variant", "Variant");
            if (string.IsNullOrWhiteSpace(name) || Number(entry, "links", "Links") >= 5 ||
                Number(entry, "itemClass", "ItemClass") == 9 ||
                (entry.TryGetProperty("corrupted", out var corrupted) || entry.TryGetProperty("Corrupted", out corrupted)) && corrupted.ValueKind == JsonValueKind.True ||
                variant?.Contains("Corrupted", StringComparison.OrdinalIgnoreCase) == true)
                continue;
            var price = Number(entry, "divineValue", "DivineValue");
            if (price is not > 0 && Find("Chaos Orb") is { } chaos)
                price = Number(entry, "chaosValue", "ChaosValue") * chaos;
            // A ground name usually lacks variant details. Use the lowest eligible base quote.
            if (price is > 0 && (!_uniquePrices.TryGetValue(name, out var previous) || price < previous))
                _uniquePrices[name] = price.Value;
        }
    }

    public double? Find(string name) =>
        name.Equals("Divine Orb", StringComparison.OrdinalIgnoreCase) ? 1 :
        _prices.TryGetValue(name, out var price) ? price : null;

    public double? FindMap(string name, int? tier)
    {
        if (!_mapPrices.TryGetValue(name, out var quotes))
            return null;
        var matching = quotes.Where(quote => tier.GetValueOrDefault() > 0 ? quote.Tier == tier : !quote.Tier.HasValue).ToArray();
        if (matching.Length == 1)
            return matching[0].Divine;
        return name == NightmareMap && quotes.Count == 1 ? quotes[0].Divine : null;
    }

    private void ReadMapPrices(JsonElement root)
    {
        if (!root.TryGetProperty("lines", out var lines) && !root.TryGetProperty("Lines", out lines))
            return;
        foreach (var entry in lines.EnumerateArray())
        {
            var name = Text(entry, "name", "Name");
            if (name == null || (entry.TryGetProperty("corrupted", out var corrupted) && corrupted.ValueKind == JsonValueKind.True) ||
                Text(entry, "variant", "Variant")?.Contains("Corrupted", StringComparison.OrdinalIgnoreCase) == true)
                continue;
            var price = Number(entry, "divineValue", "DivineValue");
            if (price is not > 0 && Find("Chaos Orb") is { } chaosRate)
                price = Number(entry, "chaosValue", "ChaosValue") * chaosRate;
            if (price is not > 0)
                continue;
            var tier = (int?)Number(entry, "mapTier", "MapTier");
            if (!_mapPrices.TryGetValue(name, out var quotes))
                _mapPrices[name] = quotes = [];
            quotes.Add((tier is > 0 ? tier : null, price.Value));
        }
    }

    private void ReadExchange(JsonElement root)
    {
        if (!root.TryGetProperty("core", out var core) || !root.TryGetProperty("lines", out var lines))
            return;

        var primary = Text(core, "primary");
        var rates = core.TryGetProperty("rates", out var rateTable) ? rateTable : default;
        var divinePerPrimary = primary == "divine" ? 1 : Number(rates, "divine");
        if (divinePerPrimary == null)
        {
            foreach (var line in lines.EnumerateArray())
            {
                if (Text(line, "id") == "divine" && Number(line, "primaryValue") is > 0 and var divineValue)
                {
                    divinePerPrimary = 1 / divineValue;
                    break;
                }
            }
        }
        if (divinePerPrimary is not > 0)
            return;

        var items = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in new[] { core, root })
        {
            if (!group.TryGetProperty("items", out var values))
                continue;
            foreach (var item in values.EnumerateArray())
            {
                if (Text(item, "id") is { } id && Text(item, "name") is { } name)
                    items[id] = name;
            }
        }

        foreach (var line in lines.EnumerateArray())
        {
            if (Text(line, "id") is { } id && items.TryGetValue(id, out var name) &&
                Number(line, "primaryValue") is > 0 and var price)
                Add(name, price * divinePerPrimary.Value);
        }
        foreach (var (id, name) in items)
        {
            if (id == primary)
                Add(name, divinePerPrimary.Value);
            else if (Number(rates, id) is > 0 and var rate)
                Add(name, divinePerPrimary.Value / rate);
        }
    }

    private void ReadFlatItemPrices(JsonElement root)
    {
        if (!root.TryGetProperty("lines", out var lines) && !root.TryGetProperty("Lines", out lines))
            return;

        // Only value a named item when the cache has a single quote for that name.
        foreach (var group in lines.EnumerateArray().Where(x => Text(x, "name", "Name") != null)
                     .GroupBy(x => Text(x, "name", "Name"), StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() == 1 && Number(group.Single(), "divineValue", "DivineValue") is > 0 and var price)
                Add(group.Key, price);
        }
    }

    private void Add(string name, double price)
    {
        if (double.IsFinite(price) && price > 0)
            _prices.TryAdd(name, price);
    }

    private static void ReadFile(string path, Action<JsonElement> parse)
    {
        try
        {
            if (!File.Exists(path))
                return;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            parse(document.RootElement);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            // Pricer may be replacing this file. The next refresh reads it again.
        }
    }

    private static string Text(JsonElement element, string name, string alternative = null)
    {
        if ((element.TryGetProperty(name, out var value) ||
             (alternative != null && element.TryGetProperty(alternative, out value))) && value.ValueKind == JsonValueKind.String)
            return value.GetString();
        return null;
    }

    private static double? Number(JsonElement element, string name, string alternative = null)
    {
        if (element.ValueKind == JsonValueKind.Object && (element.TryGetProperty(name, out var value) ||
             (alternative != null && element.TryGetProperty(alternative, out value))) &&
            value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number))
            return number;
        return null;
    }
}
