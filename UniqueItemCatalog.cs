using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace MapDropStatistics;

internal static class UniqueItemCatalog
{
    internal sealed record Entry(string Type, int? Tier);

    private static readonly Dictionary<string, Entry> Reference = LoadReference();

    public static Entry Find(string name) => Reference.GetValueOrDefault(name.Replace('’', '\''));

    public static IEnumerable<string> NamesInTier(int tier) => Reference.Where(entry => entry.Value.Tier == tier).Select(entry => entry.Key);

    public static string ItemType(string path, string itemClass = null)
    {
        var parts = path?.Split('/', '\\');
        if (parts is { Length: > 3 })
        {
            var group = parts[2];
            var type = group.ToLowerInvariant() switch
            {
                "armours" => Category(parts[3]),
                "weapons" => Category(parts[3]) ?? (parts.Length > 4 ? Category(parts[4]) : null) ?? Category(itemClass) ?? "Weapons",
                "currency" when parts[3].Equals("Strongholds", StringComparison.OrdinalIgnoreCase) => "Watchstones",
                _ => Category(group)
            };
            if (type != null)
                return type;
        }
        return Category(itemClass) ?? itemClass;
    }

    private static string Category(string value) => value?.ToLowerInvariant() switch
    {
        "belt" or "belts" => "Belts",
        "body armour" or "bodyarmours" or "bodyarmour" => "Body Armour",
        "gloves" => "Gloves",
        "boots" => "Boots",
        "helmet" or "helmets" => "Helmets",
        "shield" or "shields" => "Shields",
        "jewel" or "jewels" or "abyss jewel" or "abyssjewels" or "cluster jewel" or "clusterjewels" => "Jewels",
        "amulet" or "amulets" => "Amulets",
        "ring" or "rings" => "Rings",
        "quiver" or "quivers" => "Quivers",
        "flasks" or "flask" or "life flask" or "mana flask" or "hybrid flask" or "utility flask" => "Flasks",
        "map" or "maps" => "Maps",
        "one hand axe" or "two hand axe" or "onehandaxes" or "twohandaxes" => "Axes",
        "one hand mace" or "two hand mace" or "onehandmaces" or "twohandmaces" => "Maces",
        "one hand sword" or "two hand sword" or "thrusting one hand sword" or "onehandswords" or "twohandswords" or "rapiers" => "Swords",
        "staff" or "staves" or "warstaff" or "warstaves" => "Staves",
        "sceptre" or "sceptres" => "Sceptres",
        "dagger" or "daggers" or "rune dagger" or "runedaggers" => "Daggers",
        "claw" or "claws" => "Claws",
        "wand" or "wands" => "Wands",
        "bow" or "bows" => "Bows",
        "fishing rod" or "fishingrods" => "Fishing Rods",
        "tincture" or "tinctures" => "Tinctures",
        "idol" or "idols" => "Idols",
        _ => null
    };

    private static Dictionary<string, Entry> LoadReference()
    {
        using var stream = typeof(UniqueItemCatalog).Assembly.GetManifestResourceStream("UniqueDropTiers.json");
        var entries = stream == null ? null : JsonSerializer.Deserialize<Dictionary<string, Entry>>(stream, MapHistory.Json);
        return entries == null ? new(StringComparer.OrdinalIgnoreCase) : new(entries, StringComparer.OrdinalIgnoreCase);
    }
}
