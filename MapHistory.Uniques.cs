using System;
using System.Collections.Generic;
using System.Linq;

namespace MapDropStatistics;

internal sealed record UniqueDropsQuery(Guid[] MapIds, bool IncludeExcluded = false);
internal sealed record UniqueDrop(string Name, string Type, int? Tier, long Dropped, long Collected, long Estimated,
    double? UnitDivine = null, string Icon = null);
internal sealed record UniqueDropsResult(int Maps, long Dropped, string League, UniqueDrop[] Items);

internal sealed partial class MapHistory
{
    private readonly Dictionary<Guid, UniqueDrop[]> _uniqueDrops = [];
    private readonly IReadOnlyDictionary<string, string> _archivedUniqueTypes;

    public UniqueDropsResult GetUniqueDrops(UniqueDropsQuery query)
    {
        var totals = new Dictionary<(string Name, string Type, int? Tier),
            (long Dropped, long Collected, long Estimated, long Priced, double Value)>();
        var maps = 0;
        var dropped = 0L;
        var includeExcluded = query.IncludeExcluded && query.MapIds.Length == 1;
        string league = null;
        lock (_gate)
        {
            foreach (var id in query.MapIds.Distinct())
            {
                if (!_index.TryGetValue(id, out var map) || (map.ExcludedFromStatistics && !includeExcluded))
                    continue;

                maps++;
                dropped += map.UniquesDropped;
                if (league == null && !string.IsNullOrWhiteSpace(map.League))
                    league = map.League;
                foreach (var item in _uniqueDrops[id])
                {
                    var key = (item.Name, item.Type, item.Tier);
                    var current = totals.GetValueOrDefault(key);
                    var priced = item.UnitDivine.HasValue ? Math.Max(item.Dropped, item.Collected + item.Estimated) : 0;
                    totals[key] = (current.Dropped + item.Dropped, current.Collected + item.Collected, current.Estimated + item.Estimated,
                        current.Priced + priced, current.Value + item.UnitDivine.GetValueOrDefault() * priced);
                }
            }
        }
        return new UniqueDropsResult(maps, dropped, league, totals.Select(entry => new UniqueDrop(entry.Key.Name,
            entry.Key.Type, entry.Key.Tier, entry.Value.Dropped, entry.Value.Collected, entry.Value.Estimated,
            entry.Value.Priced == 0 ? null : entry.Value.Value / entry.Value.Priced)).ToArray());
    }

    private UniqueDrop[] IndexUniqueDrops(MapRun run) => run.Loot
        .Where(item => item.Category == "Unique" && !string.IsNullOrWhiteSpace(item.Name) &&
            (item.Dropped > 0 || item.Collected > 0 || item.Estimated > 0))
        .Select(item =>
        {
            var name = item.Name.Trim();
            var reference = UniqueItemCatalog.Find(name);
            var baseName = name.StartsWith("Foulborn ", StringComparison.OrdinalIgnoreCase) ? name["Foulborn ".Length..] : name;
            var type = item.UniqueType ?? (item.IsUniqueBelt ? "Belts" : null) ?? reference?.Type ??
                _archivedUniqueTypes.GetValueOrDefault(baseName) ??
                (baseName == name ? null : UniqueItemCatalog.Find(baseName)?.Type) ?? (IsArchivedUniqueBelt(item) ? "Belts" : null);
            return new UniqueDrop(name, type, reference?.Tier, item.Dropped, item.Collected, item.Estimated,
                LootPrice.FromItem(run, item).UnitDivine);
        }).ToArray();
}
