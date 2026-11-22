using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using SharpDX;

namespace MapDropStatistics;

public partial class MapDropStatistics
{
    private const int AltarScanIntervalMs = 100;
    private const int AltarSelectionTimeoutMs = 2500;
    private const int MaxEncounterSignalsPerRun = 128;
    private const float AltarInteractionRangeSquared = 45 * 45;
    private static readonly Regex AltarTextTags = new(@"<[^>\r\n]+>|[{}]", RegexOptions.Compiled);
    private readonly Dictionary<uint, MapAltar> _seenAltars = [];
    private readonly HashSet<uint> _visibleAltarIds = [];
    private readonly List<AltarOption> _altarOptions = [];
    private readonly HashSet<(uint AreaHash, string Source, string Detail)> _recordedEncounterSignals = [];
    private PendingAltarChoice _pendingAltar;
    private long _nextAltarScan;
    private long _altarOptionsReadAt;
    private bool _altarMouseDown;

    private sealed record AltarOption(uint Id, Entity Entity, RectangleF Bounds, System.Numerics.Vector2 Position,
        bool WasTargetable, string Text);
    private sealed record PendingAltarChoice(AltarOption Option, long ClickedAt, int MissingScans = 0);

    private void MarkEncounterArea(AreaInstance area, bool startsRun = false)
    {
        if (_activeRun == null || !IsTrackableArea(area))
            return;
        var name = area.Name ?? string.Empty;
        var areaId = area.Area?.Id;
        if (startsRun || !IsSameMapInstance(area, allowUnknown: true) || IsEncounterSignal(name) || IsEncounterSignal(areaId) ||
            name.Contains("Мираж", StringComparison.OrdinalIgnoreCase))
            RecordEncounterSignal(startsRun ? "Area" : "Area transition", areaId ?? name, area);
        if (name.Contains("Wildwood", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Дикий лес", StringComparison.OrdinalIgnoreCase))
            MarkEncounterZone("wildwood");
    }

    private static bool IsEncounterSignal(string text) => !string.IsNullOrEmpty(text) &&
        (text.Contains("Mirage", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("Varashta", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("Wildwood", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("Azmeri", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("Voodoo", StringComparison.OrdinalIgnoreCase));

    private void RecordEncounterSignal(string source, string detail, AreaInstance area = null)
    {
        if (_activeRun == null || _activeRun.EncounterSignals.Count >= MaxEncounterSignalsPerRun)
            return;
        area ??= GameController.Area.CurrentArea;
        var hash = area?.Hash ?? 0;
        if (!_recordedEncounterSignals.Add((hash, source, detail)))
            return;
        _activeRun.EncounterSignals.Add(new EncounterSignal(DateTime.UtcNow, source, detail, area?.Name ?? _currentAreaName, hash));
    }

    private void MarkEncounterZone(string zone)
    {
        if (_activeRun.Zones.Add(zone))
            _nextRunSave = 0;
    }

    private void ObserveEncounterEntity(Entity entity)
    {
        if (_activeRun == null || entity.Type is EntityType.WorldItem or EntityType.Player)
            return;

        var metadata = entity.Metadata;
        if (metadata == null)
            return;
        ObserveProgressEncounter(entity, metadata);
        if (ObserveWildwoodEntrance(metadata) || ObserveMirageNpc(metadata) || ObserveDeliriumMirror(metadata))
            return;
        if (IsEncounterSignal(metadata) &&
            !metadata.Contains("MirageArcher", StringComparison.OrdinalIgnoreCase) &&
            !metadata.Contains("MirageWarrior", StringComparison.OrdinalIgnoreCase))
            RecordEncounterSignal("Entity", metadata);
        else if (entity.Type is EntityType.AreaTransition or EntityType.Portal)
            RecordEncounterSignal("Transition entity", metadata);
        if (entity.Type != EntityType.Monster)
            RememberAltar(entity.Id, metadata);

        if (metadata.StartsWith("Metadata/MiscellaneousObjects/Azmeri/", StringComparison.Ordinal) &&
            (metadata.EndsWith("/AzmeriResourceBase", StringComparison.Ordinal) ||
             metadata.EndsWith("/AzmeriFuelResupply", StringComparison.Ordinal) ||
             metadata.Contains("/SacrificeAltarObjects/", StringComparison.Ordinal)))
            MarkEncounterZone("wildwood");
    }

    private bool ObserveWildwoodEntrance(string metadata)
    {
        if (!MapSummary.IsWildwoodEntrance(metadata))
            return false;
        MarkEncounterZone("wildwood");
        RecordEncounterSignal("Entity", metadata);
        return true;
    }

    private bool ObserveDeliriumMirror(string metadata)
    {
        if (metadata != EncounterProgressRules.DeliriumMirror)
            return false;
        MarkEncounterZone("delirium");
        RecordEncounterSignal("Entity", metadata);
        return true;
    }

    private bool ObserveMirageNpc(string metadata)
    {
        if (metadata != MapSummary.MirageNpcMetadata)
            return false;
        if (!_activeRun.MirageNpcObserved)
        {
            _activeRun.MirageNpcObserved = true;
            MarkEncounterZone("mirage");
            RecordEncounterSignal("Entity", metadata);
        }
        return true;
    }

    private void SeedEncounterEntities()
    {
        if (_activeRun == null)
            return;
        var foundMirage = _activeRun.Zones.Contains("mirage");
        var foundDelirium = _activeRun.Zones.Contains("delirium");
        var foundWildwood = _activeRun.Zones.Contains("wildwood");
        foreach (var entity in GameController.EntityListWrapper.OnlyValidEntities)
        {
            if (entity.Type is EntityType.WorldItem or EntityType.Monster or EntityType.Player)
                continue;

            var metadata = entity.Metadata;
            ObserveProgressEncounter(entity, metadata);
            foundMirage = foundMirage || ObserveMirageNpc(metadata);
            foundDelirium = foundDelirium || ObserveDeliriumMirror(metadata);
            foundWildwood = foundWildwood || ObserveWildwoodEntrance(metadata);
        }
    }

    private MapAltar RememberAltar(uint id, string metadata)
    {
        if (id == 0)
            return null;
        if (_seenAltars.TryGetValue(id, out var altar))
            return altar;
        var influence = metadata switch
        {
            "Metadata/MiscellaneousObjects/PrimordialBosses/TangleAltar" => "eater",
            "Metadata/MiscellaneousObjects/PrimordialBosses/CleansingFireAltar" => "exarch",
            _ => null
        };
        if (influence == null)
            return null;
        altar = new MapAltar(influence);
        _seenAltars.Add(id, altar);
        _activeRun.Altars.Add(altar);
        _nextAltarScan = 0;
        _nextRunSave = 0;
        return altar;
    }

    private void ResetAltarObservations()
    {
        _visibleAltarIds.Clear();
        _altarOptions.Clear();
        _pendingAltar = null;
        _nextAltarScan = 0;
        _altarOptionsReadAt = 0;
        _altarMouseDown = true;
    }

    private void UpdateEncounterTracking(long now)
    {
        var foreground = GameController.IsForeGroundCache;
        var mouseDown = foreground && Input.IsKeyDown(Keys.LButton);
        var clicked = mouseDown && !_altarMouseDown;
        _altarMouseDown = mouseDown;

        if (clicked && _pendingAltar == null && now - _altarOptionsReadAt <= AltarScanIntervalMs * 3)
        {
            var position = Input.MousePositionNum;
            var mouse = new Vector2(position.X, position.Y);
            var option = _altarOptions.FirstOrDefault(candidate => candidate.Bounds.Contains(mouse));
            if (option != null && _seenAltars.TryGetValue(option.Id, out var altar) && altar.SelectedEffects == null)
                _pendingAltar = new PendingAltarChoice(option, now);
        }

        if (now < _nextAltarScan)
            return;

        _nextAltarScan = now + AltarScanIntervalMs;
        var data = GameController.IngameState.Data;
        if (data?.IsInsideAzmeriZone == true)
        {
            MarkEncounterZone("wildwood");
            RecordEncounterSignal("Area state", "IsInsideAzmeriZone = true");
        }
        var hasOpenAltars = _seenAltars.Values.Any(altar => altar.SelectedEffects == null);
        // Labels cover instances already present when the plugin attaches, including altars seen before a portal return.
        var scanned = ReadAltarOptions(now);
        _nextAltarScan = now + (hasOpenAltars || _altarOptions.Count > 0 || _pendingAltar != null ? AltarScanIntervalMs : 450);
        ConfirmAltarChoice(now, scanned && foreground);
    }

    private bool ReadAltarOptions(long now)
    {
        _visibleAltarIds.Clear();
        _altarOptions.Clear();
        _altarOptionsReadAt = now;
        var labels = GameController.IngameState.IngameUi?.ItemsOnGroundLabelsVisible;
        if (labels == null)
            return false;
        foreach (var label in labels)
        {
            var entity = label?.ItemOnGround;
            if (entity == null || entity.Type is EntityType.WorldItem or EntityType.Monster)
                continue;
            var altar = RememberAltar(entity.Id, entity.Metadata);
            if (altar == null || label.Label is not { IsValid: true, Address: > 0, IsVisible: true } root)
                continue;
            _visibleAltarIds.Add(entity.Id);
            if (altar.SelectedEffects != null)
                continue;

            // Altar Helper reads the description from child 1 on each of the two option rows.
            var position = entity.GridPosNum;
            var targetable = entity.IsTargetable;
            for (var index = 0; index < 2; index++)
            {
                var button = root.GetChildAtIndex(index);
                var text = button?.GetChildAtIndex(1)?.GetText(1024);
                if (!string.IsNullOrWhiteSpace(text) && button.IsVisible &&
                    button.GetClientRectCache is { Width: > 0, Height: > 0 } bounds)
                    _altarOptions.Add(new AltarOption(entity.Id, entity, bounds, position, targetable, text));
            }
        }
        return true;
    }

    private void ConfirmAltarChoice(long now, bool scanned)
    {
        if (_pendingAltar is not { } pending)
            return;
        var player = GameController.Player;
        if (now - pending.ClickedAt > AltarSelectionTimeoutMs || player?.IsValid != true || player.IsDead ||
            System.Numerics.Vector2.DistanceSquared(player.GridPosNum, pending.Option.Position) > AltarInteractionRangeSquared)
        {
            _pendingAltar = null;
            return;
        }

        var entity = pending.Option.Entity;
        var consumed = entity.IsValid && (entity.GetComponent<StateMachine>()?.States
            .Any(state => state.Value > 0 && state.Name is "activated" or "used") == true ||
            pending.Option.WasTargetable && !entity.IsTargetable);
        var scans = !scanned || _visibleAltarIds.Contains(pending.Option.Id) ? 0 : pending.MissingScans + 1;
        if (!consumed && (scans < 2 || now - pending.ClickedAt < 150))
        {
            _pendingAltar = pending with { MissingScans = scans };
            return;
        }

        if (_seenAltars.TryGetValue(pending.Option.Id, out var altar) && altar.SelectedEffects == null)
        {
            altar.SelectedEffects = ParseAltarOption(pending.Option.Text);
            _nextRunSave = 0;
        }
        _pendingAltar = null;
    }

    internal static string[] ParseAltarOption(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => AltarTextTags.Replace(line, string.Empty).Trim())
            .Where(line => line.Length > 0)
            .ToArray();
}
