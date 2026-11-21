using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MapDropStatistics;

internal sealed class MapRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? SessionId { get; set; }
    public string Name { get; set; }
    public string League { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public double? Seconds { get; set; }
    public string State { get; set; } = "active";
    public bool WipeObserved { get; set; }
    public int Deaths { get; set; }
    public int PartyDeaths { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MapKillCounts Kills { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MapKillDiagnostics KillDiagnostics { get; set; }
    public int Quantity { get; set; }
    public int Rarity { get; set; }
    public int PackSize { get; set; }
    public int? MoreCurrency { get; set; }
    public int? MoreMaps { get; set; }
    public int? MoreScarabs { get; set; }
    public long? GoldCollected { get; set; }
    public MapDropTotals DropTotals { get; set; }
    public int? T0UniquesDropped { get; set; }
    public string LastT0Unique { get; set; }
    public double? ChaosDivineRate { get; set; }
    public List<MapModifier> Modifiers { get; set; } = [];
    public HashSet<string> Zones { get; set; } = [];
    public List<EncounterSignal> EncounterSignals { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool MirageNpcObserved { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool TracksUniqueBelts { get; set; }
    public List<MapAltar> Altars { get; set; }
    public List<MapLoot> Loot { get; set; } = [];
    public Dictionary<Guid, LootEdit> Edits { get; set; } = [];
    public bool? FailureOverride { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ExcludedFromStatistics { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MapPriceDivine { get; set; }

    public double? GetChaosDivineRate() => ChaosDivineRate is > 0 ? ChaosDivineRate :
        Loot.FirstOrDefault(item => item.Name == "Chaos Orb" && item.MarketUnitDivine is > 0)?.MarketUnitDivine;

    public MapKillCounts GetDisplayKills()
    {
        if (Kills == null || KillDiagnostics is not { UsesEncounterFilter: false, OmittedUniqueSamples: 0 } diagnostics ||
            Kills.Unique == 0)
            return Kills;

        // Older records can only be corrected when the saved samples account for every counted Unique.
        var countedUnique = diagnostics.UniqueSamples.Where(sample =>
            sample.Result == "Counted" && sample.ComponentRarity == "Unique").ToArray();
        if (countedUnique.Select(sample => sample.Id).Distinct().Count() != Kills.Unique || countedUnique.Length != Kills.Unique)
            return Kills;

        var kinds = countedUnique.Select(sample => MonsterKillRules.Classify(sample.Metadata, unique: true)).ToArray();
        return Kills with
        {
            Unique = kinds.Count(kind => kind == MonsterKillKind.Regular),
            EncounterUnique = kinds.Count(kind => kind == MonsterKillKind.EncounterUnique)
        };
    }
}

internal sealed class MapAltar(string influence)
{
    public string Influence { get; set; } = influence;
    public string[] SelectedEffects { get; set; }
}

internal sealed record MapModifier(string Stat, int Value, string Text, bool Visible = false);
internal sealed record EncounterSignal(DateTime SeenUtc, string Source, string Detail, string Area, uint AreaHash);
internal sealed record MapDropTotals(int Currency, int Uniques, int Normal, int Magic, int Rare);
internal sealed record MapKillCounts(int Normal = 0, int Magic = 0, int Rare = 0, int Unique = 0)
{
    public int EncounterUnique { get; init; }
    public int Total => Normal + Magic + Rare + Unique;
}
internal sealed class MapKillDiagnostics
{
    public bool UsesEncounterFilter { get; set; }
    public int IgnoredAuxiliaryDeaths { get; set; }
    public int RejectedCandidates { get; set; }
    public int OmittedUniqueSamples { get; set; }
    public List<UniqueKillSample> UniqueSamples { get; set; } = [];
}
internal sealed record UniqueKillSample(DateTime SeenUtc, uint Id, string Name, string Metadata, string Source,
    string Result, bool SeenAlive, bool Dead, int? CurrentHp, int? MaxHp, string EntityRarity, string ComponentRarity);
internal sealed record LootEdit(bool Excluded, double? UnitDivine);
internal sealed record MapLoot(Guid Id, string Name, string Category)
{
    public int Dropped { get; set; }
    public int Collected { get; set; }
    public int Estimated { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MarketUnitDivine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MapTier { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Icon { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsUniqueBelt { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string UniqueType { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, int> Collectors { get; set; }
}

internal readonly record struct LootPrice(double? UnitDivine, bool Excluded, bool RequiresUniquePrices)
{
    public static LootPrice FromItem(MapRun run, MapLoot item)
    {
        var edit = run.Edits.GetValueOrDefault(item.Id);
        return new LootPrice(edit?.UnitDivine ?? item.MarketUnitDivine, edit?.Excluded == true,
            item.Category == "Unique" && edit?.UnitDivine == null);
    }

    public bool IsIncluded(bool includeUniquePrices, double? minimumDivine) =>
        !Excluded && (!RequiresUniquePrices || includeUniquePrices) && !(UnitDivine < minimumDivine);
}

internal sealed record SessionDrop(string Name, string Category, long Count, long LastMapCount,
    double? ValueDivine, double? LastMapValueDivine, long PricedCount, string Icon = null);
internal sealed record SessionDrops(int Maps, string League, string LastMap, SessionDrop[] Items);
internal sealed record DropAverageQuery(Guid[] MapIds, string[] ItemNames);
internal sealed record DropAverageResult(int Maps, Dictionary<string, long> Drops, long CurrencyDropped, long UniquesDropped,
    long MonsterKills, int MapsWithKills, long T0UniquesDropped, int MapsWithT0, long UniqueBeltsDropped, int MapsWithBeltCounts)
{
    internal string League { get; init; }
    public Dictionary<string, string> Icons { get; init; }
}
internal enum LootScope { All, Personal, Party }
internal sealed record FarmingSession(Guid Id, DateTime StartedUtc, string Name, double HideoutSeconds = 0);
internal sealed record MapCostSettings(double MapPriceDivine = 1);
internal sealed record MapThreadUse(string Name, int Count);
internal sealed record MapThreadUsage(int Total, MapThreadUse[] Items)
{
    // The client still uses the old Scarab stat names for Thread counts by mechanic.
    internal static readonly IReadOnlyDictionary<string, string> NamesByStat = new Dictionary<string, string>
    {
        ["MapAbyssScarabCount"] = "Abyss Thread",
        ["MapBestiaryScarabCount"] = "Bestiary Thread",
        ["MapBetrayalScarabCount"] = "Betrayal Thread",
        ["MapBlightScarabCount"] = "Blight Thread",
        ["MapBreachScarabCount"] = "Breach Thread",
        ["MapExpeditionScarabCount"] = "Expedition Thread",
        ["MapIncursionScarabCount"] = "Incursion Thread",
        ["MapLegionScarabCount"] = "Legion Thread",
        ["MapRitualScarabCount"] = "Ritual Thread",
        ["MapUltimatumScarabCount"] = "Ultimatum Thread"
    };

    public static MapThreadUsage FromModifiers(IEnumerable<MapModifier> modifiers)
    {
        var total = 0;
        var items = new List<MapThreadUse>();
        foreach (var modifier in modifiers)
        {
            if (modifier.Value <= 0 || modifier.Stat == null)
                continue;
            if (modifier.Stat == "MapTotalThreadCount")
                total = modifier.Value;
            else if (NamesByStat.TryGetValue(modifier.Stat, out var name))
                items.Add(new MapThreadUse(name, modifier.Value));
        }

        return total > 0 ? new MapThreadUsage(total, items.ToArray()) : null;
    }
}
internal sealed record MinimumItemPrice(double Value, string Currency)
{
    public double? InDivine(double? chaosDivineRate) => Currency == "chaos" ? Value * chaosDivineRate : Value;
}
internal sealed record SessionBook(Guid ActiveId, Guid InitialId, Guid? PreviousId, DateTime LegacyCutoffUtc,
    bool IncludeUniquePrices, FarmingSession[] Sessions, string[] IgnoredItems = null, double? IgnoreAtOrBelowDivine = null,
    MinimumItemPrice MinimumPrice = null, MapCostSettings MapCosts = null, WidgetSettings Widget = null)
{
    public MinimumItemPrice GetMinimumPrice() =>
        MinimumPrice ?? (IgnoreAtOrBelowDivine is { } amount ? new MinimumItemPrice(amount, "divine") : null);
}
internal sealed record SessionList(Guid ActiveId, bool IncludeUniquePrices, FarmingSession[] Sessions, string[] IgnoredItems,
    MinimumItemPrice MinimumPrice, MapCostSettings MapCosts);
internal sealed record IgnoreItemChange(string Name, bool Ignored);

internal sealed record MapSummary(
    Guid Id, string Name, string League, DateTime StartedUtc, DateTime UpdatedUtc, double? Seconds,
    string State, bool Failed, int Deaths, int Quantity, int Rarity, int PackSize,
    int Dropped, int Collected, int Estimated, int UnpricedQuantity, double ProfitDivine, string[] Zones,
    Guid? SessionId, double LeftDivine, int LeftUnpricedQuantity, int? MonsterKills, MapKillCounts KillBreakdown, double? ChaosDivineRate,
    double PersonalPickupDivine, double PartyPickupDivine, int PartyDeaths, int UnpricedPersonal, int UnpricedParty,
    int DivineOrbsDropped, int UniquesDropped, int? UniqueBeltsDropped, double? MapPriceDivine, MapThreadUsage ThreadUsage)
{
    internal const string MirageNpcMetadata = "Metadata/NPC/League/Faridun/Kubera/Varashta";
    public long CurrencyDropped { get; init; }
    public int? T0UniquesDropped { get; init; }
    public string LastT0Unique { get; init; }
    public long ValdosDropped { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ExcludedFromStatistics { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MapDropBonus[] DropBonuses { get; init; }

    public static MapSummary FromRun(MapRun run, bool includeUniquePrices = false, IReadOnlySet<string> ignoredItems = null,
        MinimumItemPrice minimumPrice = null, int? uniqueBeltsDropped = null, MapThreadUsage threadUsage = null)
    {
        var chaosDivineRate = run.GetChaosDivineRate();
        var minimumDivine = minimumPrice?.InDivine(chaosDivineRate);
        var personalPickup = 0d;
        var partyPickup = 0d;
        var unpricedPersonal = 0;
        var unpricedParty = 0;
        var leftDivine = 0d;
        var leftUnpriced = 0;
        var divineOrbsDropped = 0;
        var uniquesDropped = 0;
        var currencyDropped = 0L;
        var valdos = 0L;
        foreach (var item in run.Loot)
        {
            if (item.Name == "Divine Orb")
                divineOrbsDropped += item.Dropped;
            if (item.Category == "Unique")
                uniquesDropped += item.Dropped;
            if (item.Category == "Currency")
                currencyDropped += item.Dropped;
            if (item.Name == "Valdo's Puzzle Box")
                valdos += item.Dropped;
            if (ignoredItems?.Contains(item.Name) == true)
                continue;
            var count = item.Collected + item.Estimated;
            var remaining = run.State == "legacy" ? 0 : Math.Max(item.Dropped - count, 0);
            var pricing = LootPrice.FromItem(run, item);
            if (!pricing.IsIncluded(includeUniquePrices, minimumDivine))
                continue;
            if (pricing.UnitDivine is { } price)
            {
                personalPickup += item.Collected * price;
                partyPickup += item.Estimated * price;
                leftDivine += remaining * price;
            }
            else
            {
                unpricedPersonal += item.Collected;
                unpricedParty += item.Estimated;
                leftUnpriced += remaining;
            }
        }

        var kills = run.GetDisplayKills();
        var zones = run.Zones.Where(zone => !string.Equals(zone, "mirage", StringComparison.OrdinalIgnoreCase))
            .Concat(run.Modifiers.Where(mod => mod.Value > 0).Select(mod => ZoneFromMapStat(mod.Stat)).OfType<string>())
            .ToHashSet(StringComparer.Ordinal);
        if (run.MirageNpcObserved || run.EncounterSignals.Any(signal => signal.Source == "Entity" && signal.Detail == MirageNpcMetadata))
            zones.Add("mirage");
        if (!zones.Contains("wildwood") && run.EncounterSignals.Any(signal =>
                (signal.Source is "Entity" or "Transition entity") && IsWildwoodEntrance(signal.Detail)))
            zones.Add("wildwood");

        return new MapSummary(run.Id, run.Name, run.League, run.StartedUtc, run.UpdatedUtc, run.Seconds,
            run.State, run.FailureOverride ?? run.WipeObserved, run.Deaths + run.PartyDeaths, run.Quantity, run.Rarity, run.PackSize,
            run.Loot.Sum(x => x.Dropped), run.Loot.Sum(x => x.Collected), run.Loot.Sum(x => x.Estimated),
            unpricedPersonal + unpricedParty, personalPickup + partyPickup,
            zones.ToArray(),
            run.SessionId, leftDivine, leftUnpriced, kills?.Total, kills, chaosDivineRate,
            personalPickup, partyPickup, run.PartyDeaths, unpricedPersonal, unpricedParty,
            divineOrbsDropped, run.DropTotals?.Uniques ?? uniquesDropped, uniqueBeltsDropped, run.MapPriceDivine, threadUsage)
        {
            CurrencyDropped = run.DropTotals?.Currency ?? currencyDropped,
            T0UniquesDropped = run.T0UniquesDropped,
            LastT0Unique = run.LastT0Unique,
            ExcludedFromStatistics = run.ExcludedFromStatistics,
            ValdosDropped = valdos
        };
    }

    internal static bool IsWildwoodEntrance(string metadata) => metadata is
        "Metadata/Terrain/Leagues/Azmeri/WoodsEntrance" or
        "Metadata/Terrain/Leagues/Azmeri/WoodsEntranceTransition";

    public static string ZoneFromMapStat(string stat) => stat switch
    {
        "MapAzmeriLeague" => "wildwood",
        "MapSpawnAfflictionMirror" or "MapHasAfflictionEverywhere" => "delirium",
        "MapMirageChosenWishIndex1ForLogging" => "mirage",
        "MapBeyondSpawnInitiator" or "MapBeyondSpawnInitiatorPaleFaction" or
            "MapBeyondSpawnInitiatorFleshFaction" or "MapBeyondSpawnInitiatorDemonFaction" => "beyond",
        _ => null
    };
}

internal sealed partial class MapHistory
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly LootScope[] LootScopes = Enum.GetValues<LootScope>();
    private readonly string _directory;
    private readonly IReadOnlySet<string> _uniqueBeltNames;
    private readonly Func<string, bool> _isT0Unique;
    private readonly object _gate = new();
    private Dictionary<Guid, MapSummary> _index = [];
    private Dictionary<Guid, MapSummary> _indexWithUniques = [];
    private readonly Dictionary<Guid, LootCounts[]> _sessionLoot = [];
    private readonly Dictionary<Guid, LiveMapEdits> _liveMapEdits = [];
    private readonly Dictionary<(Guid, LootScope), SessionDrops> _sessionDrops = [];
    private readonly HashSet<Guid> _unpricedRuns = [];
    private SessionBook _sessions;
    private HashSet<string> _ignoredItems;

    public MapHistory(string directory, Action<string> logError, IReadOnlySet<string> uniqueBeltNames = null,
        Func<string, bool> isT0Unique = null, WidgetSettings widgetDefaults = null,
        IReadOnlyDictionary<string, string> archivedUniqueTypes = null)
    {
        _directory = directory;
        _uniqueBeltNames = uniqueBeltNames ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _isT0Unique = isT0Unique;
        _archivedUniqueTypes = archivedUniqueTypes ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(_directory);
        _sessions = LoadSessions(logError);
        _liveWidget = new LiveWidget(_sessions.Widget ?? widgetDefaults ?? new WidgetSettings(), null);
        _ignoredItems = new HashSet<string>(_sessions.IgnoredItems ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                var run = Read(file);
                if (MapAreaRules.IsExcludedFromMaps(run.Name))
                    continue;
                if (run.State is "active" or "paused")
                {
                    run.State = run.State == "paused" ? "recorded" : "interrupted";
                    Write(run);
                }
                Index(run);
            }
            catch (Exception ex)
            {
                logError($"Map history could not load {Path.GetFileName(file)}: {ex.Message}");
            }
        }
    }

    public static Guid LegacyId(string fileName) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(fileName.ToUpperInvariant())).AsSpan(0, 16));

    public MapSummary[] List()
    {
        lock (_gate)
            return CurrentIndex.Values.OrderByDescending(run => run.StartedUtc).ToArray();
    }

    public MapSummary GetSummary(Guid id)
    {
        lock (_gate)
            return CurrentIndex.GetValueOrDefault(id);
    }

    public string[] ListLeagues()
    {
        lock (_gate)
            return _index.Values.Select(map => map.League).Where(league => !string.IsNullOrWhiteSpace(league))
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private Dictionary<Guid, MapSummary> CurrentIndex => _sessions.IncludeUniquePrices ? _indexWithUniques : _index;

    public Guid ActiveSessionId
    {
        get { lock (_gate) return _sessions.ActiveId; }
    }

    public SessionList ListSessions()
    {
        lock (_gate)
            return new SessionList(_sessions.ActiveId, _sessions.IncludeUniquePrices,
                _sessions.Sessions.Select(session => session.Name switch
                {
                    "Первая сессия" => session with { Name = "First session" },
                    "До последнего сброса" => session with { Name = "Before last reset" },
                    _ => session
                }).ToArray(), _sessions.IgnoredItems ?? [], _sessions.GetMinimumPrice(), _sessions.MapCosts ?? new MapCostSettings());
    }

    public SessionDrops GetSessionDrops(Guid? sessionId = null, LootScope scope = LootScope.All)
    {
        lock (_gate)
        {
            var id = sessionId ?? _sessions.ActiveId;
            if (id != Guid.Empty && !_sessions.Sessions.Any(session => session.Id == id))
                return null;
            if (_sessionDrops.TryGetValue((id, scope), out var cached))
                return cached;

            var maps = _sessionLoot.Keys.Select(runId => _index[runId])
                .Where(map => !map.ExcludedFromStatistics && (id == Guid.Empty || map.SessionId == id))
                .OrderByDescending(map => map.StartedUtc).ToArray();
            var lastMap = maps.FirstOrDefault(map => map.State != "active");
            var minimumPrice = _sessions.GetMinimumPrice();
            var totals = new Dictionary<(string Name, string Category), SessionItemTotals>();
            foreach (var map in maps)
            {
                var minimumDivine = minimumPrice?.InDivine(map.ChaosDivineRate);
                var isLastMap = map.Id == lastMap?.Id;
                foreach (var item in _sessionLoot[map.Id])
                {
                    var count = item.Count(scope);
                    if (count <= 0)
                        continue;

                    var key = (item.Name, item.Category);
                    if (!totals.TryGetValue(key, out var itemTotals))
                        totals[key] = itemTotals = new SessionItemTotals();
                    var price = !_ignoredItems.Contains(item.Name) &&
                        item.Price.IsIncluded(_sessions.IncludeUniquePrices, minimumDivine) ? item.Price.UnitDivine : null;
                    itemTotals.Add(count, price, isLastMap);
                }
            }
            var items = totals.Select(entry => new SessionDrop(entry.Key.Name, entry.Key.Category,
                entry.Value.Count, entry.Value.LastMapCount, entry.Value.ValueDivine, entry.Value.LastMapValueDivine,
                entry.Value.PricedCount)).ToArray();
            var league = maps.FirstOrDefault(map => !string.IsNullOrWhiteSpace(map.League))?.League;
            return _sessionDrops[(id, scope)] = new SessionDrops(maps.Length, league, lastMap?.Name, items);
        }
    }

    public DropAverageResult GetDropAverages(DropAverageQuery query)
    {
        var totals = query.ItemNames.Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(name => name, _ => 0L, StringComparer.OrdinalIgnoreCase);
        var maps = 0;
        var currency = 0L;
        var uniques = 0L;
        var kills = 0L;
        var mapsWithKills = 0;
        var t0 = 0L;
        var mapsWithT0 = 0;
        var belts = 0L;
        var mapsWithBeltCounts = 0;
        string league = null;
        lock (_gate)
        {
            foreach (var id in query.MapIds.Distinct())
            {
                if (!_sessionLoot.TryGetValue(id, out var loot))
                    continue;
                var map = _index[id];
                if (map.ExcludedFromStatistics || map.State == "active")
                    continue;
                if (league == null && !string.IsNullOrWhiteSpace(map.League))
                    league = map.League;
                maps++;
                currency += map.CurrencyDropped;
                uniques += map.UniquesDropped;
                if (map.State != "paused")
                {
                    belts += _uniqueDrops[id].Where(item => item.Type == "Belts").Sum(item => item.Dropped);
                    mapsWithBeltCounts++;
                }
                if (map.MonsterKills is { } monsterKills)
                {
                    kills += monsterKills;
                    mapsWithKills++;
                }
                if (map.T0UniquesDropped is { } tierZero)
                {
                    t0 += tierZero;
                    mapsWithT0++;
                }
                if (totals.Count == 0)
                    continue;
                foreach (var item in loot)
                {
                    if (item.Dropped > 0 && totals.TryGetValue(item.Name, out var current))
                        totals[item.Name] = current + item.Dropped;
                }
            }
        }
        return new DropAverageResult(maps, totals, currency, uniques, kills, mapsWithKills, t0, mapsWithT0, belts, mapsWithBeltCounts)
            { League = league };
    }

    private sealed class SessionItemTotals
    {
        public long Count { get; private set; }
        public long LastMapCount { get; private set; }
        public long PricedCount { get; private set; }
        public double? ValueDivine { get; private set; }
        public double? LastMapValueDivine { get; private set; }

        public void Add(long count, double? unitPrice, bool isLastMap)
        {
            Count += count;
            if (isLastMap)
                LastMapCount += count;
            if (unitPrice is not { } price)
                return;
            var value = count * price;
            PricedCount += count;
            ValueDivine = ValueDivine.GetValueOrDefault() + value;
            if (isLastMap)
                LastMapValueDivine = LastMapValueDivine.GetValueOrDefault() + value;
        }
    }

    private sealed record LootCounts(string Name, string Category, int Dropped, int Collected, int Estimated, LootPrice Price)
    {
        public long Count(LootScope scope) => scope switch
        {
            LootScope.Personal => Collected,
            LootScope.Party => Estimated,
            _ => Dropped
        };
    }

    private sealed record LiveMapEdits(Dictionary<Guid, LootEdit> Loot, bool? Failed, bool Excluded, double? Price)
    {
        public static LiveMapEdits FromRun(MapRun run) => new(run.Edits, run.FailureOverride, run.ExcludedFromStatistics, run.MapPriceDivine);
    }

    public Guid StartNewSession()
    {
        lock (_gate)
        {
            var session = new FarmingSession(Guid.NewGuid(), DateTime.UtcNow, null);
            ChangeActiveSession(session.Id, session);
            return session.Id;
        }
    }

    public bool ActivateSession(Guid id)
    {
        lock (_gate)
        {
            if (!_sessions.Sessions.Any(session => session.Id == id))
                return false;
            if (_sessions.ActiveId != id)
                ChangeActiveSession(id);
            return true;
        }
    }

    private void ChangeActiveSession(Guid id, FarmingSession added = null)
    {
        var now = Environment.TickCount64;
        CheckpointHideout(now);
        SaveSessions(_sessions with { ActiveId = id, Sessions = added == null ? _sessions.Sessions : [.. _sessions.Sessions, added] });
        if (_hideoutStartedAt.HasValue)
            _hideoutStartedAt = now;
        if (_widgetContext is { } context)
            PublishWidget(context with { SessionId = id });
    }

    public void SetUniquePrices(bool include)
    {
        lock (_gate)
        {
            if (_sessions.IncludeUniquePrices == include)
                return;
            SaveSessions(_sessions with { IncludeUniquePrices = include });
            _sessionDrops.Clear();
            _widgetSessionStats = null;
        }
    }

    public void SetIgnoredItem(string name, bool ignored)
    {
        lock (_gate)
        {
            var names = new HashSet<string>(_ignoredItems, StringComparer.OrdinalIgnoreCase);
            if (!(ignored ? names.Add(name) : names.Remove(name)))
                return;

            UpdateValuations(_sessions with { IgnoredItems = names.Order(StringComparer.OrdinalIgnoreCase).ToArray() }, names);
        }
    }

    public void SetMinimumPrice(MinimumItemPrice minimumPrice)
    {
        lock (_gate)
        {
            if (_sessions.GetMinimumPrice() != minimumPrice || _sessions.IgnoreAtOrBelowDivine.HasValue)
                UpdateValuations(_sessions with { MinimumPrice = minimumPrice, IgnoreAtOrBelowDivine = null }, _ignoredItems);
        }
    }

    private void UpdateValuations(SessionBook settings, HashSet<string> names)
    {
        var standard = new Dictionary<Guid, MapSummary>(_index.Count);
        var withUniques = new Dictionary<Guid, MapSummary>(_index.Count);
        var minimumPrice = settings.GetMinimumPrice();
        foreach (var (id, existing) in _index)
        {
            var run = Read(FilePath(id));
            standard[id] = MapSummary.FromRun(run, false, names, minimumPrice, existing.UniqueBeltsDropped, existing.ThreadUsage)
                with { SessionId = existing.SessionId, T0UniquesDropped = existing.T0UniquesDropped, LastT0Unique = existing.LastT0Unique,
                    DropBonuses = existing.DropBonuses };
            withUniques[id] = MapSummary.FromRun(run, true, names, minimumPrice, existing.UniqueBeltsDropped, existing.ThreadUsage)
                with { SessionId = existing.SessionId, T0UniquesDropped = existing.T0UniquesDropped, LastT0Unique = existing.LastT0Unique,
                    DropBonuses = existing.DropBonuses };
        }

        SaveSessions(settings);
        _ignoredItems = names;
        _index = standard;
        _indexWithUniques = withUniques;
        _sessionDrops.Clear();
        _widgetSessionStats = null;
    }

    public MapRun Get(Guid id)
    {
        lock (_gate)
            return _index.ContainsKey(id) ? Read(FilePath(id)) : null;
    }

    public void Save(MapRun run)
    {
        if (MapAreaRules.IsExcludedFromMaps(run.Name))
            return;

        lock (_gate)
        {
            Write(run);
            Index(run);
        }
    }

    public bool EditLoot(Guid id, Guid itemId, LootEdit edit)
    {
        return Edit(id, run =>
        {
            if (run.State == "legacy" || !run.Loot.Any(x => x.Id == itemId && (x.Dropped > 0 || x.Collected + x.Estimated > 0)))
                return false;
            if (!edit.Excluded && edit.UnitDivine == null)
                run.Edits.Remove(itemId);
            else
                run.Edits[itemId] = edit;
            return true;
        });
    }

    public bool EditFailure(Guid id, bool? failed) => Edit(id, run =>
    {
        run.FailureOverride = failed;
        return true;
    });

    public bool EditStatisticsExclusion(Guid id, bool excluded)
    {
        lock (_gate)
        {
            if (!_index.TryGetValue(id, out var current))
                return false;
            if (current.ExcludedFromStatistics == excluded)
                return true;
            if (!Edit(id, run =>
                {
                    run.ExcludedFromStatistics = excluded;
                    return true;
                }))
                return false;
            if (_widgetContext != null)
                PublishWidget(_widgetContext);
            return true;
        }
    }

    public void SetMapCosts(MapCostSettings costs)
    {
        lock (_gate)
        {
            if (_sessions.MapCosts != costs)
                SaveSessions(_sessions with { MapCosts = costs });
        }
    }

    public bool EditMapPrice(Guid id, double? price) => Edit(id, run =>
    {
        run.MapPriceDivine = price;
        return true;
    });

    public void FillSavedCounters(Guid id, MapDropTotals totals, int moreCurrency, int moreMaps, int moreScarabs, int? t0 = null) => Edit(id, run =>
    {
        if (run.DropTotals != null && run.MoreCurrency.HasValue && run.MoreMaps.HasValue && run.MoreScarabs.HasValue &&
            (run.T0UniquesDropped.HasValue || !t0.HasValue))
            return false;
        run.DropTotals ??= totals;
        run.MoreCurrency ??= moreCurrency;
        run.MoreMaps ??= moreMaps;
        run.MoreScarabs ??= moreScarabs;
        run.T0UniquesDropped ??= t0;
        return true;
    }, touchTimestamp: false);

    public void FillMarketPrices(Func<string, NinjaPriceCache> getPrices)
    {
        Guid[] candidates;
        lock (_gate)
            candidates = _unpricedRuns.ToArray();
        foreach (var id in candidates)
        {
            var league = GetSummary(id)?.League;
            if (string.IsNullOrWhiteSpace(league) || getPrices(league) is not { } prices)
                continue;
            Edit(id, run =>
            {
                var changed = false;
                if (!run.GetChaosDivineRate().HasValue && prices.Find("Chaos Orb") is { } chaos)
                {
                    run.ChaosDivineRate = chaos;
                    changed = true;
                }
                foreach (var item in run.Loot.Where(NeedsMarketPrice))
                {
                    var quote = item.Name == "Chaos Orb" ? run.GetChaosDivineRate() : prices.FindLoot(item);
                    if (quote is not { } price)
                        continue;
                    item.MarketUnitDivine = price;
                    changed = true;
                }
                return changed;
            }, touchTimestamp: false);
        }
    }

    private bool Edit(Guid id, Func<MapRun, bool> change, bool touchTimestamp = true)
    {
        lock (_gate)
        {
            var run = Get(id);
            if (run == null || !change(run))
                return false;
            if (touchTimestamp)
                run.UpdatedUtc = DateTime.UtcNow;
            Save(run);
            return true;
        }
    }

    public void SaveLive(MapRun run)
    {
        lock (_gate)
        {
            if (!_liveMapEdits.TryGetValue(run.Id, out var edits) && Get(run.Id) is { } saved)
                edits = LiveMapEdits.FromRun(saved);
            if (edits != null)
            {
                run.Edits = edits.Loot;
                run.FailureOverride = edits.Failed;
                run.ExcludedFromStatistics = edits.Excluded;
                run.MapPriceDivine = edits.Price;
            }
            Save(run);
        }
    }

    private void Index(MapRun run)
    {
        if (run.State is "active" or "paused")
            _liveMapEdits[run.Id] = LiveMapEdits.FromRun(run);
        else
            _liveMapEdits.Remove(run.Id);
        var sessionId = ResolveSession(run);
        if (run.State != "active" || _index.TryGetValue(run.Id, out var old) && old.State != "active")
            _widgetSessionStats = null;
        var minimumPrice = _sessions.GetMinimumPrice();
        var beltDrops = CountUniqueBelts(run);
        var threadUsage = MapThreadUsage.FromModifiers(run.Modifiers);
        var t0 = run.T0UniquesDropped;
        var lastT0 = run.LastT0Unique;
        if (t0 != 0 && (t0 == null || lastT0 == null) && _isT0Unique != null &&
            (run.DropTotals != null || run.Loot.Count > 0))
        {
            var drops = run.Loot.Where(item => item.Dropped > 0 && item.Category == "Unique" &&
                !string.IsNullOrWhiteSpace(item.Name) && _isT0Unique(item.Name)).ToArray();
            t0 ??= drops.Sum(item => item.Dropped);
            var names = drops.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
            if (names.Length == 1)
                lastT0 ??= names[0];
        }
        var dropBonuses = MapDropBonuses.Read(run);
        _index[run.Id] = MapSummary.FromRun(run, false, _ignoredItems, minimumPrice, beltDrops, threadUsage)
            with { SessionId = sessionId, T0UniquesDropped = t0, LastT0Unique = lastT0, DropBonuses = dropBonuses };
        _indexWithUniques[run.Id] = MapSummary.FromRun(run, true, _ignoredItems, minimumPrice, beltDrops, threadUsage)
            with { SessionId = sessionId, T0UniquesDropped = t0, LastT0Unique = lastT0, DropBonuses = dropBonuses };
        _uniqueDrops[run.Id] = IndexUniqueDrops(run);
        if (run.State is not ("active" or "paused" or "legacy") &&
            (!run.GetChaosDivineRate().HasValue || run.Loot.Any(NeedsMarketPrice)))
            _unpricedRuns.Add(run.Id);
        else
            _unpricedRuns.Remove(run.Id);
        if (!sessionId.HasValue)
            return;
        _sessionLoot[run.Id] = run.Loot.Where(item => item.Dropped > 0 || item.Collected + item.Estimated > 0)
            .Select(item => new LootCounts(item.Name, item.Category, item.Dropped, item.Collected, item.Estimated,
                LootPrice.FromItem(run, item))).ToArray();
        foreach (var scope in LootScopes)
        {
            _sessionDrops.Remove((sessionId.Value, scope));
            _sessionDrops.Remove((Guid.Empty, scope));
        }
    }

    private int? CountUniqueBelts(MapRun run)
    {
        if (run.TracksUniqueBelts)
            return run.Loot.Where(item => item.IsUniqueBelt).Sum(item => item.Dropped);
        if (run.DropTotals?.Uniques == 0)
            return 0;
        if (_uniqueBeltNames.Count == 0 || !run.Loot.Any(item => item.Category == "Unique"))
            return null;

        return run.Loot.Where(IsArchivedUniqueBelt).Sum(item => item.Dropped);
    }

    public void AnnotateArchivedBelts(MapRun run)
    {
        if (run.TracksUniqueBelts)
            return;
        foreach (var item in run.Loot.Where(IsArchivedUniqueBelt))
            item.IsUniqueBelt = true;
    }

    private bool IsArchivedUniqueBelt(MapLoot item)
    {
        if (item.Category != "Unique" || string.IsNullOrWhiteSpace(item.Name))
            return false;
        var name = item.Name.Trim();
        return _uniqueBeltNames.Contains(name) ||
            (name.StartsWith("Foulborn ", StringComparison.OrdinalIgnoreCase) && _uniqueBeltNames.Contains(name["Foulborn ".Length..]));
    }

    internal static bool NeedsMarketPrice(MapLoot item) =>
        (item.Dropped > 0 || item.Collected + item.Estimated > 0) && !item.MarketUnitDivine.HasValue;

    private Guid? ResolveSession(MapRun run) => run.State == "legacy" ? null :
        run.SessionId ?? (run.StartedUtc < _sessions.LegacyCutoffUtc ? _sessions.PreviousId : null) ?? _sessions.InitialId;

    private SessionBook LoadSessions(Action<string> logError)
    {
        var cutoff = DateTime.MinValue;
        try
        {
            if (File.Exists(SessionsPath))
                return JsonSerializer.Deserialize<SessionBook>(File.ReadAllBytes(SessionsPath), Json) ??
                       throw new JsonException("Empty session archive");
            if (File.Exists(SessionStartPath))
                cutoff = JsonSerializer.Deserialize<DateTime>(File.ReadAllBytes(SessionStartPath), Json);
        }
        catch (Exception ex)
        {
            logError($"Map history could not load sessions: {ex.Message}");
            if (File.Exists(SessionsPath))
                throw;
        }

        var initial = new FarmingSession(Guid.NewGuid(), cutoff == DateTime.MinValue ? DateTime.UtcNow : cutoff, "First session");
        FarmingSession previous = cutoff == DateTime.MinValue ? null : new FarmingSession(Guid.NewGuid(), cutoff, "Before last reset");
        var book = new SessionBook(initial.Id, initial.Id, previous?.Id, cutoff, false,
            previous == null ? [initial] : [previous, initial]);
        WriteAtomic(SessionsPath, book);
        return book;
    }

    private void SaveSessions(SessionBook update)
    {
        WriteAtomic(SessionsPath, update);
        _sessions = update;
    }

    private string SessionStartPath => Path.Combine(_directory, "session-start");
    private string SessionsPath => Path.Combine(_directory, "sessions");
    private string FilePath(Guid id) => Path.Combine(_directory, $"{id:N}.json");

    private static MapRun Read(string file)
    {
        using var stream = File.OpenRead(file);
        return JsonSerializer.Deserialize<MapRun>(stream, Json) ?? throw new JsonException("Empty map record");
    }

    private void Write(MapRun run) => WriteAtomic(FilePath(run.Id), run);

    internal static void WriteAtomic<T>(string file, T data, JsonSerializerOptions options = null)
    {
        var temporary = file + ".tmp";
        using (var stream = File.Create(temporary))
            JsonSerializer.Serialize(stream, data, options ?? Json);
        File.Move(temporary, file, overwrite: true);
    }
}
