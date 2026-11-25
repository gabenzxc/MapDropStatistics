using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;

namespace MapDropStatistics;

internal sealed record WidgetOption(string Id, string Label);
internal sealed record WidgetSettings(bool Enabled = true, bool ShowOutsideMaps = true, bool ShowOverPanels = false,
    int X = 2, int Y = 7, int Opacity = 90, bool Compact = false, string[] HiddenRows = null, string[] CustomItems = null)
{
    internal static readonly WidgetOption[] Options =
    [
        new("maps", "Maps and failures"),
        new("map-time", "Map time"),
        new("hideout-time", "Hideout time"),
        new("currency", "Currency"),
        new("uniques", "Uniques"),
        new("t0", "T0 Uniques"),
        new("last-t0", "Last T0"),
        new("kills", "Monster kills"),
        new("divine", "Divine Orbs"),
        new("valdo", "Valdo's Puzzle Box"),
        new("profit", "Pickup value"),
        new("modifiers", "Item quantity and rarity"),
        new("custom", "Custom items"),
        new("legion", "Legion progress"),
        new("abyss", "Abyss progress"),
        new("breach", "Breach progress"),
        new("wildwood", "Wildwood entry"),
        new("mirage", "Mirage activation"),
        new("delirium", "Delirium activation")
    ];

    public bool Shows(string id) => HiddenRows?.Contains(id, StringComparer.Ordinal) != true;
}

internal sealed record WidgetProgress(int Completed, int Found)
{
    public string Text => $"{Completed}/{Found}";
}

internal sealed record WidgetRow(string Id, string Label, string Current, string Session = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WidgetProgress Progress = null)
{
    public bool Inline => Id == "last-t0";
    public int AccentRgb => Id switch
    {
        "map-time" or "hideout-time" => 0x83D1CE,
        "currency" or "divine" or "valdo" or "profit" or "custom" => 0xEACB7D,
        "uniques" => 0xE5A174,
        "t0" or "last-t0" => 0xF4B6D5,
        "kills" => 0xEE9295,
        "modifiers" => 0xA0B7F8,
        "legion" => 0xD1A7EF,
        "abyss" => 0x90D9AB,
        "breach" => 0xEEA7D9,
        "wildwood" => 0xAECF79,
        "mirage" => 0xE8C183,
        "delirium" => 0xB3C7D7,
        _ => 0xB6D3A2
    };
}
internal sealed record WidgetData(Guid SessionId, string Area, bool InGame, bool InMap, double HideoutSeconds, WidgetRow[] Rows,
    bool MapExcluded = false);
internal sealed record LiveWidget(WidgetSettings Settings, WidgetData Data)
{
    public WidgetOption[] Options => WidgetSettings.Options;
}

internal sealed partial class MapHistory
{
    private const int HideoutCheckpointMs = 30_000;
    private LiveWidget _liveWidget;
    private WidgetSessionStats _widgetSessionStats;
    private WidgetContext _widgetContext;
    private long? _hideoutStartedAt;
    private long _hideoutSavedThrough;

    private sealed record WidgetSessionStats(Guid SessionId, DropAverageResult Drops, int Failed, double MapSeconds,
        int TimedMaps, double PickupValue, double Quantity, double Rarity, string LastT0Unique);
    private sealed record WidgetContext(Guid SessionId, Guid? MapId, string Area, bool InGame, bool InMap,
        double? MapSeconds, Guid? MapSessionId);

    public LiveWidget ReadWidget() => Volatile.Read(ref _liveWidget);

    public LiveWidget ReadWidgetForDashboard()
    {
        lock (_gate)
        {
            var widget = _liveWidget;
            if (widget.Data is not { } data || data.SessionId != _sessions.ActiveId)
                return widget;

            var now = Environment.TickCount64;
            if (_hideoutStartedAt.HasValue && now - _hideoutSavedThrough >= HideoutCheckpointMs)
                CheckpointHideout(now);
            var session = _sessions.Sessions.First(candidate => candidate.Id == data.SessionId);
            var (current, total) = HideoutTimes(session, now);
            var rows = data.Rows.Select(row => row.Id == "hideout-time" ? HideoutRow(current, total) : row).ToArray();
            return widget with { Data = data with { HideoutSeconds = total, Rows = rows } };
        }
    }

    public void ObserveHideout(bool inHideout)
    {
        lock (_gate)
        {
            var now = Environment.TickCount64;
            if (!_hideoutStartedAt.HasValue)
            {
                if (inHideout)
                    _hideoutStartedAt = _hideoutSavedThrough = now;
                return;
            }

            if (!inHideout || now - _hideoutSavedThrough >= HideoutCheckpointMs)
                CheckpointHideout(now);
            if (!inHideout)
                _hideoutStartedAt = null;
        }
    }

    private void CheckpointHideout(long now)
    {
        if (!_hideoutStartedAt.HasValue)
            return;
        AddHideoutTime(_sessions.ActiveId, (now - _hideoutSavedThrough) / 1000d);
        _hideoutSavedThrough = now;
    }

    private (double Current, double Total) HideoutTimes(FarmingSession session, long now) => _hideoutStartedAt is { } started
        ? ((now - started) / 1000d, session.HideoutSeconds + (now - _hideoutSavedThrough) / 1000d)
        : (0, session.HideoutSeconds);

    private static WidgetRow HideoutRow(double current, double total) => new("hideout-time", "Hideout", Time(current), Time(total));

    public void SetWidgetSettings(WidgetSettings settings)
    {
        lock (_gate)
        {
            SaveSessions(_sessions with { Widget = settings });
            _widgetSessionStats = null;
            Volatile.Write(ref _liveWidget, _liveWidget with { Settings = settings });
            if (_widgetContext != null)
                PublishWidget(_widgetContext);
        }
    }

    public void AddHideoutTime(Guid sessionId, double seconds)
    {
        lock (_gate)
        {
            var index = Array.FindIndex(_sessions.Sessions, session => session.Id == sessionId);
            if (!(seconds > 0) || index < 0)
                return;
            var sessions = (FarmingSession[])_sessions.Sessions.Clone();
            sessions[index] = sessions[index] with { HideoutSeconds = sessions[index].HideoutSeconds + seconds };
            SaveSessions(_sessions with { Sessions = sessions });
        }
    }

    public void PublishWidget(Guid sessionId, Guid? mapId, string area, bool inGame, bool inMap, double? mapSeconds,
        Guid? mapSessionId = null) =>
        PublishWidget(new WidgetContext(sessionId, mapId, area, inGame, inMap, mapSeconds, mapSessionId));

    private void PublishWidget(WidgetContext context)
    {
        lock (_gate)
        {
            var (sessionId, mapId, area, inGame, inMap, mapSeconds, mapSessionId) = context;
            var session = _sessions.Sessions.FirstOrDefault(candidate => candidate.Id == sessionId);
            if (session == null || sessionId != _sessions.ActiveId)
                return;
            _widgetContext = context;

            var settings = _liveWidget.Settings;
            var names = settings.CustomItems ?? [];
            var statNames = new[] { "Divine Orb", "Valdo's Puzzle Box" }.Concat(names).ToArray();
            var totals = GetWidgetSessionStats(sessionId, statNames);
            var current = mapId is { } id ? CurrentIndex.GetValueOrDefault(id) : null;
            var mapExcluded = current?.ExcludedFromStatistics == true ||
                (mapId.HasValue && IsMapDeleted(mapId.Value, mapSessionId));
            if (mapExcluded)
            {
                current = null;
                mapSeconds = null;
            }
            var drops = totals.Drops;
            var (currentHideout, sessionHideout) = HideoutTimes(session, Environment.TickCount64);
            var rows = new List<WidgetRow>
            {
                new("maps", "Maps", Count(drops.Maps), $"{Count(totals.Failed)} failed"),
                new("map-time", "Map time", Time(mapSeconds ?? current?.Seconds),
                    totals.TimedMaps == 0 ? null : Time(totals.MapSeconds / totals.TimedMaps)),
                HideoutRow(currentHideout, sessionHideout),
                new("currency", "Currency", Count(current?.CurrencyDropped), drops.Maps == 0 ? null :
                    Count((long)Math.Round((double)drops.CurrencyDropped / drops.Maps, MidpointRounding.AwayFromZero))),
                new("uniques", "Uniques", Count(current?.UniquesDropped), Average(drops.UniquesDropped, drops.Maps)),
                new("t0", "T0 Uniques", Count(current?.T0UniquesDropped), Average(drops.T0UniquesDropped, drops.MapsWithT0)),
                new("last-t0", "Last T0", current?.LastT0Unique ?? totals.LastT0Unique ?? "—"),
                new("kills", "Kills", Count(current?.MonsterKills), Average(drops.MonsterKills, drops.MapsWithKills)),
                new("divine", "Divine Orbs", Count(current?.DivineOrbsDropped), Average(drops.Drops["Divine Orb"], drops.Maps)),
                new("valdo", "Valdo's Box", Count(current?.ValdosDropped), Average(drops.Drops["Valdo's Puzzle Box"], drops.Maps)),
                new("profit", "Pickup value", current == null ? "—" : $"{Number(current.ProfitDivine)} div",
                    drops.Maps == 0 ? null : $"{Tenths(totals.PickupValue / drops.Maps)} div"),
                new("modifiers", "IIQ/IIR",
                    current == null ? "—" : Percentages(current.Quantity, current.Rarity),
                    drops.Maps == 0 ? null : Percentages(totals.Quantity / drops.Maps, totals.Rarity / drops.Maps))
            };

            var currentDrops = current != null && _sessionLoot.TryGetValue(current.Id, out var loot) ? loot : [];
            foreach (var name in names)
                rows.Add(new WidgetRow("custom", name, current == null ? "—" : Count(currentDrops
                    .Where(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)).Sum(item => (long)item.Dropped)),
                    Average(drops.Drops.GetValueOrDefault(name), drops.Maps)));
            if (inMap && current != null)
                AddEncounterRows(rows, current);

            Volatile.Write(ref _liveWidget, new LiveWidget(settings,
                new WidgetData(sessionId, area, inGame, inMap, sessionHideout, rows.ToArray(), mapExcluded)));
        }
    }

    private static void AddEncounterRows(List<WidgetRow> rows, MapSummary map)
    {
        var progress = map.EncounterProgress;
        if (progress?.LegionSeen > 0)
        {
            var legion = new WidgetProgress(progress.LegionClosed, progress.LegionSeen);
            rows.Add(new WidgetRow("legion", "Legion", legion.Text, Progress: legion));
        }
        if (progress?.AbyssSeen > 0)
        {
            var abyss = new WidgetProgress(progress.AbyssClosed, progress.AbyssSeen);
            rows.Add(new WidgetRow("abyss", "Abyss", abyss.Text, Progress: abyss));
        }
        if (progress?.BreachSeen > 0)
        {
            var breach = new WidgetProgress(progress.BreachClosed, progress.BreachSeen);
            rows.Add(new WidgetRow("breach", "Breach", breach.Text, Progress: breach));
        }
        if (map.Zones.Contains("wildwood"))
            rows.Add(new WidgetRow("wildwood", "Wildwood", progress?.WildwoodEntered == true ? "Entered" : "Not entered"));
        if (map.Zones.Contains("mirage"))
            rows.Add(new WidgetRow("mirage", "Mirage", progress?.MirageActivated == true ? "Activated" : "Not activated"));
        if (map.Zones.Contains("delirium"))
            rows.Add(new WidgetRow("delirium", "Delirium", progress?.DeliriumStarted == true ? "Started" : "Not started"));
    }

    private WidgetSessionStats GetWidgetSessionStats(Guid sessionId, string[] names)
    {
        if (_widgetSessionStats is { } cached && cached.SessionId == sessionId)
            return cached;

        var completed = CurrentIndex.Values.Where(map => map.SessionId == sessionId && !map.ExcludedFromStatistics && map.State != "active").ToArray();
        var timed = completed.Where(map => map.Seconds > 0).ToArray();
        var drops = GetDropAverages(new DropAverageQuery(completed.Select(map => map.Id).ToArray(), names));
        return _widgetSessionStats = new WidgetSessionStats(sessionId, drops, completed.Count(map => map.Failed),
            timed.Sum(map => map.Seconds.Value), timed.Length, completed.Sum(map => map.ProfitDivine),
            completed.Sum(map => (double)map.Quantity), completed.Sum(map => (double)map.Rarity),
            completed.Where(map => map.T0UniquesDropped > 0).MaxBy(map => map.StartedUtc)?.LastT0Unique);
    }

    private static string Count(long? value) => value?.ToString("N0", CultureInfo.InvariantCulture) ?? "—";

    private static string Number(double value) => value is > 0 and < 0.001 ? "<0.001" :
        value.ToString(value is > 0 and < 0.01 ? "0.###" : "0.##", CultureInfo.InvariantCulture);

    private static string Tenths(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero)
        .ToString("0.#", CultureInfo.InvariantCulture);

    private static string Average(long total, int maps) => maps > 0 ? Tenths((double)total / maps) : null;

    private static string Percentages(double quantity, double rarity) =>
        $"{Tenths(quantity)}%/{Math.Round(rarity, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)}%";

    private static string Time(double? value)
    {
        if (value == null)
            return "—";
        var seconds = (long)Math.Max(value.Value, 0);
        return seconds >= 3600 ? $"{seconds / 3600}h {seconds / 60 % 60}m" : $"{seconds / 60}m {seconds % 60}s";
    }
}
