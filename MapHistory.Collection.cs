using System;
using System.Collections.Generic;
using System.Linq;

namespace MapDropStatistics;

internal sealed record CollectionItem(string Name, long PickedUp, string Icon = null);
internal sealed record CollectionTier(string Name, long Dropped, long PickedUp);
internal sealed record CollectionRecord(string Map, DateTime StartedUtc, long Count);
internal sealed record CollectionRecords(CollectionRecord DivineOrbs, CollectionRecord MonsterKills, CollectionRecord UniqueDrops);
internal sealed record CollectionSummary(int Runs, CollectionItem[] Currency, CollectionItem[] T0Uniques, CollectionTier[] Tiers,
    CollectionRecords Records)
{
    internal string League { get; init; }
}

internal sealed partial class MapHistory
{
    private const string CollectionDivineOrb = "Divine Orb";
    private static readonly string[] CollectionCurrencyNames =
    [
        "Mirror of Kalandra", "Hinekora's Lock", CollectionDivineOrb, "Sacred Orb",
        "Valdo's Puzzle Box", "Voidborn Reliquary Key", "Fracturing Orb"
    ];
    private static readonly string[] CollectionTierNames = ["T0", "T1", "T2", "T3", "T4", "T5", "Unknown"];
    private CollectionSummary _collection;

    public CollectionSummary GetCollection()
    {
        lock (_gate)
            return _collection ??= BuildCollection();
    }

    private CollectionSummary BuildCollection()
    {
        var currency = CollectionCurrencyNames.ToDictionary(name => name, _ => 0L, StringComparer.OrdinalIgnoreCase);
        var t0Names = UniqueItemCatalog.NamesInTier(0).Concat(_sessions.T0UniqueNames)
            .Distinct(StringComparer.InvariantCultureIgnoreCase).OrderByDescending(name => name.Length).ToArray();
        var t0 = t0Names.ToDictionary(name => name, _ => 0L, StringComparer.InvariantCultureIgnoreCase);
        var t0Matches = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
        var tiers = new (long Dropped, long PickedUp)[CollectionTierNames.Length];
        const int unknownTier = 6;
        var runs = 0;
        MapSummary latest = null;
        CollectionRecord divineRecord = null, killRecord = null, uniqueRecord = null;

        foreach (var (id, map) in _index)
        {
            if (map.ExcludedFromStatistics)
                continue;
            runs++;
            if (!string.IsNullOrWhiteSpace(map.League) && (latest == null || map.StartedUtc > latest.StartedUtc))
                latest = map;

            long pickedDivines = 0;
            if (_sessionLoot.TryGetValue(id, out var loot))
            {
                foreach (var item in loot)
                {
                    if (item.Collected > 0 && item.Name?.Replace('’', '\'') is { } name && currency.TryGetValue(name, out var count))
                    {
                        currency[name] = count + item.Collected;
                        if (string.Equals(name, CollectionDivineOrb, StringComparison.OrdinalIgnoreCase))
                            pickedDivines += item.Collected;
                    }
                }
            }

            if (map.State is not ("active" or "paused"))
            {
                divineRecord = BestRecord(divineRecord, map, pickedDivines);
                killRecord = BestRecord(killRecord, map, map.MonsterKills.GetValueOrDefault());
                uniqueRecord = BestRecord(uniqueRecord, map, map.UniquesDropped);
            }

            long detailed = 0, namedT0 = 0;
            foreach (var item in _uniqueDrops[id])
            {
                detailed += item.Dropped;
                if (!t0Matches.TryGetValue(item.Name, out var t0Name))
                    t0Matches[item.Name] = t0Name = FindT0UniqueName(t0Names, item.Name);
                var tier = item.Tier is >= 0 and < unknownTier ? item.Tier.Value : unknownTier;
                if (t0Name != null)
                {
                    t0[t0Name] += item.Collected;
                    tier = 0;
                    namedT0 += item.Dropped;
                }
                tiers[tier].Dropped += item.Dropped;
                tiers[tier].PickedUp += item.Collected;
            }

            // Older runs can have aggregate counts for uniques without saved item details.
            var missing = Math.Max(0, map.UniquesDropped - detailed);
            var missingT0 = Math.Clamp(map.T0UniquesDropped.GetValueOrDefault() - namedT0, 0, missing);
            tiers[0].Dropped += missingT0;
            tiers[unknownTier].Dropped += missing - missingT0;
        }

        var currencies = CollectionCurrencyNames.Select(name => new CollectionItem(name, currency[name])).ToArray();
        var uniques = t0.OrderBy(item => item.Key, StringComparer.InvariantCultureIgnoreCase)
            .Select(item => new CollectionItem(item.Key, item.Value)).ToArray();
        var tierTotals = CollectionTierNames.Select((name, tier) => new CollectionTier(name, tiers[tier].Dropped, tiers[tier].PickedUp)).ToArray();
        return new CollectionSummary(runs, currencies, uniques, tierTotals, new(divineRecord, killRecord, uniqueRecord))
            { League = latest?.League };
    }

    private static CollectionRecord BestRecord(CollectionRecord current, MapSummary map, long count) =>
        count > 0 && (current == null || count > current.Count || count == current.Count && map.StartedUtc > current.StartedUtc)
            ? new CollectionRecord(map.Name, map.StartedUtc, count) : current;
}
