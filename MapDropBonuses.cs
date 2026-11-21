using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MapDropStatistics;

internal sealed record MapDropBonus(string Label, long Percent, string Stat, string Source = null);

internal static partial class MapDropBonuses
{
    private static readonly Dictionary<string, (string Subject, bool Increased)> Stats = new(StringComparer.Ordinal)
    {
        ["MapCurrencyDropChancePctFinalFromUberMod"] = ("Currency", false),
        ["MapCurrencyDropChancePctFinalFromFaridunWish"] = ("Currency", false),
        ["MapMapItemDropChancePctFinalFromUberMod"] = ("Maps", false),
        ["MapMapDropChancePctFinalFromFaridunWish"] = ("Maps", false),
        ["MapScarabDropChancePctFinalFromUberMod"] = ("Threads", false),
        ["MapScarabDropChancePctFinalFromFaridunWish"] = ("Threads", false),
        ["MapDivinationCardDropChancePctFinalFromQuality"] = ("Divination Cards", false),
        ["MapDivinationCardDropChancePctFinalFromFaridunWish"] = ("Divination Cards", false),
        ["ChartItemDropRarityPctFinalFromBorderModifier"] = ("Item Rarity", false),
        ["MapMapItemDropChancePct"] = ("Maps", true),
        ["MapDropScalingPct"] = ("Atlas Drops", true),
        ["More Currency"] = ("Currency", false),
        ["More Maps"] = ("Maps", false),
        ["More Scarabs"] = ("Threads", false)
    };

    [GeneratedRegex(@"\[([^\[\]|]+)\|([^\[\]]+)\]")]
    private static partial Regex Markup();

    [GeneratedRegex(@"^[\d.,]+% (more|less|increased|reduced) (.+?) (?:found in (?:this )?Area|in this Area)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BonusText();

    public static MapDropBonus[] Read(MapRun run)
    {
        if (run.Modifiers.Count == 0 && run.MoreCurrency == null && run.MoreMaps == null && run.MoreScarabs == null)
            return null;

        var bonuses = new List<MapDropBonus>();
        var captured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var modifier in run.Modifiers)
        {
            if (modifier.Value == 0 || string.IsNullOrEmpty(modifier.Stat))
                continue;
            long value = modifier.Value;
            if (!Stats.TryGetValue(modifier.Stat, out var kind))
            {
                var match = BonusText().Match(Markup().Replace(modifier.Text ?? string.Empty, "$2").Trim());
                if (!match.Success)
                    continue;
                var action = match.Groups[1].Value.ToLowerInvariant();
                kind = (NormalizeSubject(match.Groups[2].Value), action is "increased" or "reduced");
                value = (action is "less" or "reduced" ? -1 : 1) * Math.Abs(value);
            }

            var wish = modifier.Stat.EndsWith("FromFaridunWish", StringComparison.Ordinal);
            if (!wish)
                captured.Add(kind.Subject);
            bonuses.Add(Create(kind.Subject, value, kind.Increased, modifier.Stat, wish ? "Mirage wish" : null));
        }

        foreach (var (subject, value) in new[] { ("Currency", run.MoreCurrency), ("Maps", run.MoreMaps), ("Threads", run.MoreScarabs) })
        {
            if (value is { } percent && percent != 0 && !captured.Contains(subject))
                bonuses.Add(Create(subject, percent, false, null, null));
        }
        return bonuses.ToArray();
    }

    private static string NormalizeSubject(string name) => name.Trim().ToLowerInvariant() switch
    {
        "thread" or "threads" or "scarab" or "scarabs" => "Threads",
        "currency" => "Currency",
        "maps" => "Maps",
        "divination cards" => "Divination Cards",
        "item rarity" => "Item Rarity",
        "atlas drops" => "Atlas Drops",
        _ => name.Trim()
    };

    private static MapDropBonus Create(string subject, long value, bool increased, string stat, string source)
    {
        var prefix = increased ? (value > 0 ? "Increased" : "Reduced") : (value > 0 ? "More" : "Less");
        return new MapDropBonus($"{prefix} {subject}", Math.Abs(value), stat, source);
    }
}
