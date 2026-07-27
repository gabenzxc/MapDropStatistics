using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Threading;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.PoEMemory.Models;
using ExileCore.Shared.Nodes;
using ExileCore.Shared.Enums;
using SharpDX;

namespace MapDropStatistics;

public partial class MapDropStatistics : BaseSettingsPlugin<MapDropStatisticsSettings>
{
    private const string SaveLockedAreaName = "Monastery of the Keepers";
    private const string SessionSnapshotFileName = "last_session_summary.json";
    private const string ProvisionalCategoryCurrency = "currency";
    private const string ProvisionalCategoryFragment = "fragment";
    private const int PendingRetryIntervalMs = 100;
    private const int PendingEntryTtlMs = 3000;
    private const int SessionSnapshotIntervalMs = 2000;
    private static readonly string[] MapStatNoiseTokens =
    [
        "boss",
        "chest",
        "strongbox",
        "incursion",
        "perandus",
        "blight",
        "delirium",
        "harbinger",
        "ritual",
        "legion",
        "expedition",
        "bestiary",
        "betrayal",
        "abyss",
        "breach",
        "synthesis",
        "heist",
        "torment",
        "possessed",
        "touched",
        "influenced",
        "influence",
        "magicmonster",
        "raremonster",
        "uniquemonster",
        "master",
        "playermodifier",
        "players",
        "donotapply",
        "mapboss",
        "mapsoffered",
        "mission"
    ];
    private readonly HashSet<string> _seenPersistentItemKeys = [];
    private const string T0UniqueFileName = "t0_uniques.txt";
    private const string DefaultUniqueArtMappingFileName = "uniqueArtMapping.default.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };
    private readonly Dictionary<long, PendingDropInfo> _pendingDropKeys = [];
    private readonly AreaLootStats _currentAreaStats = new();
    private readonly SessionLootStats _sessionStats = new();
    private readonly TrackedDropWindowStats _trackedDropWindowStats = new();
    private readonly List<AreaReview> _appliedAreaReviews = [];
    private readonly List<string> _savedMapStatFiles = [];
    private HashSet<string> _t0UniqueNames = new(StringComparer.InvariantCultureIgnoreCase);
    private Dictionary<string, List<string>> _uniqueArtMapping = new(StringComparer.InvariantCultureIgnoreCase);

    private string _currentAreaName = "No area";
    private string _currentAreaKey = string.Empty;
    private string _selectedSavedMapStatPath = string.Empty;
    private string _savedMapStatsFilter = string.Empty;
    private AreaDump _selectedAreaDump;
    private bool _trackingCurrentArea;
    private bool _hasPausedTrackableArea;
    private bool _hasLastKnownMapModifierSnapshot;
    private bool _pendingResumeFingerprintValidation;
    private int _lastAreaChangeCount = -1;
    private long _nextPendingRetryTick;
    private long _nextSessionSnapshotTick;
    private DateTime _currentAreaStartedUtc;
    private bool _isInNonMapArea;
    private DateTime _nonMapAreaStartedUtc;
    private int _entityAddedAttempts;
    private int _pendingRetryAttempts;
    private bool _wasInGameLastTick;
    private MapModifierSnapshot _lastKnownMapModifierSnapshot;
    private MapModifierSnapshot _expectedResumeMapModifierSnapshot;

    public override bool Initialise()
    {
        try
        {
            Name = "Map Drop Statistics";
            Settings.Actions.ResetSessionAverage.OnPressed += ResetSessionStats;
            Settings.Actions.AddPendingMapToAverage.OnPressed += UndoLastAppliedMap;
            Settings.Actions.ClearSavedMapStats.OnPressed += ClearSavedMapStats;
            Settings.Actions.LoadLastSession.OnPressed += LoadLastSessionSnapshot;
            EnsureCustomTrackedItemDefaults();
            EnsureTrackedDropWindowDefaults();
            _t0UniqueNames = LoadT0UniqueNames();
            _uniqueArtMapping = LoadUniqueArtMapping();
            RefreshSavedMapStats();
            if (Settings.Actions.AutoLoadLastSession)
                LoadLastSessionSnapshot();
            _lastAreaChangeCount = GameController?.Game?.AreaChangeCount ?? -1;
            StartTrackingArea(GameController?.Area?.CurrentArea);
            return true;
        }
        catch (Exception ex)
        {
            LogError($"Initialise failed: {ex}");
            return false;
        }
    }

    public override void AreaChange(AreaInstance area)
    {
        try
        {
            if (_uniqueArtMapping.Count == 0)
                _uniqueArtMapping = LoadUniqueArtMapping();
            HandleAreaTransition(area);
        }
        catch (Exception ex)
        {
            LogError($"AreaChange failed: {ex}");
        }
    }

    public override void EntityAdded(Entity entity)
    {
        try
        {
            if (!_trackingCurrentArea || entity?.IsValid != true || GameController?.Files?.BaseItemTypes == null)
                return;

            if (!ValidatePendingResumeFingerprintIfReady() && _pendingResumeFingerprintValidation)
                return;

            if (!entity.TryGetComponent<WorldItem>(out var worldItem))
                return;

            var itemEntity = worldItem.ItemEntity;
            if (itemEntity?.IsValid != true || string.IsNullOrEmpty(itemEntity.Path))
                return;

            var dropKey = GetDropKey(entity, itemEntity);
            var persistentItemKey = GetPersistentItemKey(entity, itemEntity);
            if (dropKey == 0 || string.IsNullOrEmpty(persistentItemKey) || _seenPersistentItemKeys.Contains(persistentItemKey))
            {
                return;
            }

            _entityAddedAttempts++;
            TryProcessDrop(dropKey, persistentItemKey, entity, itemEntity);
        }
        catch (Exception ex)
        {
            LogError($"EntityAdded failed for {entity?.Path ?? "unknown"}: {ex}");
        }
    }

    public override void Render()
    {
        try
        {
            if (!Settings.Enable || GameController?.InGame != true || GameController.IsLoading)
                return;

            if (!ShouldDraw())
                return;

            if (_trackingCurrentArea || Settings.Tracking.ShowWhenNotTrackingArea)
                DrawOverlay(BuildLines());

            DrawTrackedDropWindow();
        }
        catch (Exception ex)
        {
            LogError($"Render failed: {ex}");
        }
    }

    public override void DrawSettings()
    {
        try
        {
            base.DrawSettings();
            DrawSavedMapStatsViewer();
        }
        catch (Exception ex)
        {
            LogError($"DrawSettings failed: {ex}");
        }
    }

    public override Job Tick()
    {
        try
        {
            if (GameController?.InGame != true || GameController.IsLoading)
            {
                if (_wasInGameLastTick)
                    SaveSessionSnapshot();

                _wasInGameLastTick = false;
                return null;
            }

            _wasInGameLastTick = true;

            EnsureAreaState();

            if (!_trackingCurrentArea)
                return null;

            UpdateCurrentAreaMapModifierStats();

            var now = Environment.TickCount64;
            if (now >= Interlocked.Read(ref _nextPendingRetryTick))
            {
                Interlocked.Exchange(ref _nextPendingRetryTick, now + PendingRetryIntervalMs);
                RetryPendingDrops();
            }

            SaveSessionSnapshotIfNeeded();
        }
        catch (Exception ex)
        {
            LogError($"Tick scan failed: {ex}");
        }

        return null;
    }

    private void EnsureAreaState()
    {
        var liveArea = GameController?.Area?.CurrentArea;
        var liveAreaKey = GetAreaKey(liveArea);
        var liveAreaChangeCount = GameController?.Game?.AreaChangeCount ?? _lastAreaChangeCount;

        if (!_trackingCurrentArea && _hasPausedTrackableArea && !IsTrackableArea(liveArea))
        {
            if (!_isInNonMapArea)
                StartNonMapAreaSegment();

            _lastAreaChangeCount = liveAreaChangeCount;
            return;
        }

        if (liveAreaChangeCount != _lastAreaChangeCount)
        {
            HandleAreaTransition(liveArea);
            return;
        }

        if (string.IsNullOrEmpty(liveAreaKey) || string.Equals(liveAreaKey, _currentAreaKey, StringComparison.Ordinal))
            return;

        HandleAreaTransition(liveArea);
    }

    private void HandleAreaTransition(AreaInstance area)
    {
        FinalizeNonMapSegment();

        var nextAreaKey = GetAreaKey(area);
        var nextIsTrackable = IsTrackableArea(area);

        if (_trackingCurrentArea &&
            !string.IsNullOrEmpty(_currentAreaKey) &&
            string.Equals(_currentAreaKey, nextAreaKey, StringComparison.Ordinal))
        {
            _currentAreaName = area?.Name ?? _currentAreaName;
            _lastAreaChangeCount = GameController?.Game?.AreaChangeCount ?? _lastAreaChangeCount;
            return;
        }

        if (!_trackingCurrentArea)
        {
            if (!nextIsTrackable)
            {
                StartNonMapAreaSegment();
                _lastAreaChangeCount = GameController?.Game?.AreaChangeCount ?? _lastAreaChangeCount;
                return;
            }

            if (_hasPausedTrackableArea)
            {
                var readResult = ReadCurrentMapModifierSnapshot();
                if (HasSameMapModifierFingerprint(readResult))
                {
                    ResumePausedArea(area);
                    return;
                }

                if (_hasLastKnownMapModifierSnapshot && readResult.MatchedStatsCount == 0)
                {
                    ResumePausedArea(area, validateFingerprintWhenReady: true);
                    return;
                }

                FinalizeCurrentArea();
            }

            StartTrackingArea(area, resetStats: true);
            return;
        }

        if (nextIsTrackable)
        {
            if (CanContinueCurrentArea(area))
            {
                ResumePausedArea(area);
                return;
            }

            FinalizeCurrentArea();
            StartTrackingArea(area, resetStats: true);
            return;
        }

        PauseCurrentArea();
        StartNonMapAreaSegment();
        _lastAreaChangeCount = GameController?.Game?.AreaChangeCount ?? _lastAreaChangeCount;
    }

    private void TryProcessDrop(long dropKey, string persistentItemKey, Entity worldEntity, Entity itemEntity)
    {
        if (TryProcessKnownCurrencyByPath(dropKey, persistentItemKey, worldEntity, itemEntity))
            return;

        var baseItemType = GameController.Files.BaseItemTypes.Translate(itemEntity.Path);
        if (baseItemType == null || string.IsNullOrEmpty(baseItemType.BaseName))
        {
            SavePending(dropKey, persistentItemKey, worldEntity, itemEntity, "base_type_missing");
            return;
        }

        if (ShouldIgnoreItem(itemEntity.Path, baseItemType))
        {
            _seenPersistentItemKeys.Add(persistentItemKey);
            _pendingDropKeys.Remove(dropKey);
            return;
        }

        if (string.Equals(baseItemType.BaseName, "Gold", StringComparison.InvariantCultureIgnoreCase) ||
            string.Equals(baseItemType.BaseName, "Rogue's Marker", StringComparison.InvariantCultureIgnoreCase))
        {
            _seenPersistentItemKeys.Add(persistentItemKey);
            _pendingDropKeys.Remove(dropKey);
            return;
        }

        if (IsFragment(baseItemType, itemEntity.Path))
        {
            if (!itemEntity.TryGetComponent<Stack>(out var fragmentStack))
            {
                if (!HasPendingProvisionalCount(dropKey, ProvisionalCategoryFragment, baseItemType.BaseName))
                {
                    CountFragmentDrop(baseItemType.BaseName, 1);
                    CountCustomTrackedItem(baseItemType.BaseName, 1);
                    CountTrackedDropWindowItem(baseItemType.BaseName, 1);
                }

                SavePending(dropKey, persistentItemKey, worldEntity, itemEntity, "fragment_stack_missing", ProvisionalCategoryFragment, 1, baseItemType.BaseName);
                return;
            }

            var quantity = Math.Max(fragmentStack.Size, 1);
            CountFragmentDrop(baseItemType.BaseName, quantity);
            CountCustomTrackedItem(baseItemType.BaseName, quantity);
            CountTrackedDropWindowItem(baseItemType.BaseName, quantity);

            _seenPersistentItemKeys.Add(persistentItemKey);
            _pendingDropKeys.Remove(dropKey);
            return;
        }

        if (IsCurrency(baseItemType, itemEntity.Path))
        {
            if (!itemEntity.TryGetComponent<Stack>(out var stack))
            {
                if (!HasPendingProvisionalCount(dropKey, ProvisionalCategoryCurrency, baseItemType.BaseName))
                {
                    CountCurrencyDrop(baseItemType.BaseName, 1);
                    CountCustomTrackedItem(baseItemType.BaseName, 1);
                    CountTrackedDropWindowItem(baseItemType.BaseName, 1);
                }

                SavePending(dropKey, persistentItemKey, worldEntity, itemEntity, "currency_stack_missing", ProvisionalCategoryCurrency, 1, baseItemType.BaseName);
                return;
            }

            var quantity = Math.Max(stack.Size, 1);
            CountCurrencyDrop(baseItemType.BaseName, quantity);
            CountCustomTrackedItem(baseItemType.BaseName, quantity);
            CountTrackedDropWindowItem(baseItemType.BaseName, quantity);

            _seenPersistentItemKeys.Add(persistentItemKey);
            _pendingDropKeys.Remove(dropKey);
            return;
        }

        if (!itemEntity.TryGetComponent<Mods>(out var mods))
        {
            SavePending(dropKey, persistentItemKey, worldEntity, itemEntity, "mods_missing");
            return;
        }

        if (mods.ItemRarity == ItemRarity.Unique)
        {
            var uniqueName = GetUniqueDisplayName(itemEntity, baseItemType, mods);
            _currentAreaStats.UniqueItems++;
            _currentAreaStats.AddDrop($"Unique::{uniqueName}", 1);
            CountCustomTrackedItem(uniqueName, 1);
            var isT0Unique = IsT0Unique(uniqueName);
            CountTrackedDropWindowItem(uniqueName, 1, isT0Unique);

            if (isT0Unique)
            {
                _currentAreaStats.T0UniqueItems++;
                _currentAreaStats.LastT0UniqueName = uniqueName;
            }
        }
        else
        {
            CountGearBaseDrop(itemEntity, baseItemType, mods.ItemRarity);
            CountCustomTrackedItem(baseItemType.BaseName, 1);
            CountTrackedDropWindowItem(baseItemType.BaseName, 1);
        }

        _seenPersistentItemKeys.Add(persistentItemKey);
        _pendingDropKeys.Remove(dropKey);
    }

    private bool TryProcessKnownCurrencyByPath(long dropKey, string persistentItemKey, Entity worldEntity, Entity itemEntity)
    {
        var baseName = itemEntity?.Path switch
        {
            "Metadata/Items/Currency/CurrencyModValues" => "Divine Orb",
            "Metadata/Items/Currency/CurrencyValdoPuzzleBox" => "Valdo's Puzzle Box",
            _ => string.Empty
        };

        if (string.IsNullOrEmpty(baseName))
            return false;

        if (itemEntity.TryGetComponent<Stack>(out var stack))
        {
            var quantity = Math.Max(stack.Size, 1);
            CountCurrencyDrop(baseName, quantity);
            CountCustomTrackedItem(baseName, quantity);
            CountTrackedDropWindowItem(baseName, quantity);
            _seenPersistentItemKeys.Add(persistentItemKey);
            _pendingDropKeys.Remove(dropKey);
            return true;
        }

        if (!HasPendingProvisionalCount(dropKey, ProvisionalCategoryCurrency, baseName))
        {
            CountCurrencyDrop(baseName, 1);
            CountCustomTrackedItem(baseName, 1);
            CountTrackedDropWindowItem(baseName, 1);
        }

        SavePending(dropKey, persistentItemKey, worldEntity, itemEntity, "known_currency_stack_missing", ProvisionalCategoryCurrency, 1, baseName);
        return true;
    }

    private void RetryPendingDrops()
    {
        if (_pendingDropKeys.Count == 0)
            return;

        var nowUtc = DateTime.UtcNow;
        foreach (var pending in _pendingDropKeys.Values.ToList())
        {
            if ((nowUtc - pending.LastSeenUtc).TotalMilliseconds > PendingEntryTtlMs)
            {
                _pendingDropKeys.Remove(pending.DropKey);
                continue;
            }

            var worldEntity = pending.WorldEntity;
            var itemEntity = pending.ItemEntity;
            if (worldEntity?.IsValid != true || itemEntity?.IsValid != true)
            {
                _pendingDropKeys.Remove(pending.DropKey);
                continue;
            }

            if (_seenPersistentItemKeys.Contains(pending.PersistentItemKey))
            {
                _pendingDropKeys.Remove(pending.DropKey);
                continue;
            }

            if (pending.ProvisionalStackQuantity > 0 &&
                itemEntity.TryGetComponent<Stack>(out var stack))
            {
                var actualQuantity = Math.Max(stack.Size, 1);
                var delta = actualQuantity - pending.ProvisionalStackQuantity;
                if (delta > 0 && !string.IsNullOrWhiteSpace(pending.BaseName))
                {
                    switch (pending.ProvisionalStackCategory)
                    {
                        case ProvisionalCategoryCurrency:
                            CountCurrencyDrop(pending.BaseName, delta);
                            break;
                        case ProvisionalCategoryFragment:
                            CountFragmentDrop(pending.BaseName, delta);
                            break;
                    }

                    CountCustomTrackedItem(pending.BaseName, delta);
                    CountTrackedDropWindowItem(pending.BaseName, delta);
                }

                _seenPersistentItemKeys.Add(pending.PersistentItemKey);
                _pendingDropKeys.Remove(pending.DropKey);
                continue;
            }

            _pendingRetryAttempts++;
            TryProcessDrop(pending.DropKey, pending.PersistentItemKey, worldEntity, itemEntity);
        }
    }

    private static bool IsCurrency(BaseItemType baseItemType, string path)
    {
        if (!string.IsNullOrEmpty(path) && path.StartsWith("Metadata/Items/Currency/", StringComparison.Ordinal))
            return true;

        return baseItemType.ClassName?.Contains("Currency", StringComparison.InvariantCultureIgnoreCase) == true;
    }

    private static bool IsFragment(BaseItemType baseItemType, string path)
    {
        var className = baseItemType.ClassName ?? string.Empty;
        var baseName = baseItemType.BaseName ?? string.Empty;

        if (!string.IsNullOrEmpty(path) && path.StartsWith("Metadata/Items/MapFragments/", StringComparison.Ordinal))
            return true;

        return className == "MapFragment" ||
               className == "VaultKey" ||
               baseName.Contains("Timeless ", StringComparison.InvariantCultureIgnoreCase) ||
               baseName.StartsWith("Simulacrum", StringComparison.InvariantCultureIgnoreCase) ||
               baseName.StartsWith("Crescent Splinter", StringComparison.InvariantCultureIgnoreCase) ||
               (string.Equals(className, "StackableCurrency", StringComparison.InvariantCultureIgnoreCase) &&
                baseName.StartsWith("Splinter of ", StringComparison.InvariantCultureIgnoreCase));
    }

    private static bool ShouldIgnoreItem(string path, BaseItemType baseItemType)
    {
        if (!string.IsNullOrEmpty(path) && path.StartsWith("Metadata/Items/Gems/", StringComparison.Ordinal))
            return true;

        var className = baseItemType.ClassName ?? string.Empty;
        return className.Contains("Gem", StringComparison.InvariantCultureIgnoreCase);
    }

    private void CountCurrencyDrop(string baseName, int quantity)
    {
        _currentAreaStats.CurrencyStacks++;
        _currentAreaStats.CurrencyQuantity += quantity;
        _currentAreaStats.AddDrop($"Currency::{baseName}", quantity);

        if (string.Equals(baseName, "Divine Orb", StringComparison.InvariantCultureIgnoreCase))
            _currentAreaStats.DivineOrbQuantity += quantity;

        if (string.Equals(baseName, "Valdo's Puzzle Box", StringComparison.InvariantCultureIgnoreCase))
            _currentAreaStats.ValdosPuzzleBoxQuantity += quantity;
    }

    private void CountFragmentDrop(string baseName, int quantity)
    {
        _currentAreaStats.FragmentStacks++;
        _currentAreaStats.FragmentQuantity += quantity;
        _currentAreaStats.AddDrop($"Fragment::{baseName}", quantity);
    }

    private void CountGearBaseDrop(Entity itemEntity, BaseItemType baseItemType, ItemRarity rarity)
    {
        if (!IsGearBaseItem(itemEntity, baseItemType) || rarity is not (ItemRarity.Normal or ItemRarity.Magic or ItemRarity.Rare))
            return;

        switch (rarity)
        {
            case ItemRarity.Normal:
                _currentAreaStats.NormalBaseItems++;
                _currentAreaStats.AddDrop("Base::Normal", 1);
                break;
            case ItemRarity.Magic:
                _currentAreaStats.MagicBaseItems++;
                _currentAreaStats.AddDrop("Base::Magic", 1);
                break;
            case ItemRarity.Rare:
                _currentAreaStats.RareBaseItems++;
                _currentAreaStats.AddDrop("Base::Rare", 1);
                break;
        }
    }

    private static bool IsGearBaseItem(Entity itemEntity, BaseItemType baseItemType)
    {
        var className = baseItemType.ClassName ?? string.Empty;
        return itemEntity?.HasComponent<Weapon>() == true ||
               itemEntity?.HasComponent<Armour>() == true ||
               className is "Quiver" or "Ring" or "Amulet" or "Belt" or "Jewel" or "AbyssJewel";
    }

    private void CountCustomTrackedItem(string itemName, int quantity)
    {
        if (string.IsNullOrWhiteSpace(itemName) || quantity <= 0)
            return;

        if (!GetConfiguredCustomTrackedItems().Contains(itemName, StringComparer.InvariantCultureIgnoreCase))
            return;

        _currentAreaStats.CustomTrackedCounts[itemName] =
            _currentAreaStats.CustomTrackedCounts.TryGetValue(itemName, out var existing)
                ? existing + quantity
                : quantity;
    }

    private IReadOnlyList<string> GetConfiguredCustomTrackedItems()
    {
        return (Settings.Tracking.CustomTrackedCurrencyItems.Content ?? [])
            .Select(x => x?.Value?.Trim() ?? string.Empty)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.InvariantCultureIgnoreCase)
            .ToArray();
    }

    private void EnsureCustomTrackedItemDefaults()
    {
        if (Settings.Tracking.CustomTrackedCurrencyItems.Content.Count == 0)
            Settings.Tracking.CustomTrackedCurrencyItems.Content.Add(new TextNode("Sacred Orb"));
    }

    private void EnsureTrackedDropWindowDefaults()
    {
        if (Settings.TrackedDropWindow.CustomTrackedItems.Content.Count == 0)
            Settings.TrackedDropWindow.CustomTrackedItems.Content.Add(new TextNode("Sacred Orb"));
    }

    private IReadOnlyList<string> GetConfiguredTrackedDropWindowItems()
    {
        return (Settings.TrackedDropWindow.CustomTrackedItems.Content ?? [])
            .Select(x => x?.Value?.Trim() ?? string.Empty)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.InvariantCultureIgnoreCase)
            .ToArray();
    }

    private void CountTrackedDropWindowItem(string itemName, int quantity, bool isT0Unique = false)
    {
        if (string.IsNullOrWhiteSpace(itemName) || quantity <= 0)
            return;

        if (!isT0Unique &&
            !GetConfiguredTrackedDropWindowItems().Contains(itemName, StringComparer.InvariantCultureIgnoreCase))
        {
            return;
        }

        _trackedDropWindowStats.Add(itemName, quantity);
    }

    private int GetCurrentTrackedItemCount(string itemName)
    {
        return _currentAreaStats.CustomTrackedCounts.TryGetValue(itemName, out var quantity)
            ? quantity
            : 0;
    }

    private void StartTrackingArea(AreaInstance area, bool resetStats = true)
    {
        UpdateLiveTimers();

        if (resetStats)
        {
            _seenPersistentItemKeys.Clear();
            _pendingDropKeys.Clear();
            _currentAreaStats.Reset();
            _hasLastKnownMapModifierSnapshot = false;
            _lastKnownMapModifierSnapshot = default;
            _pendingResumeFingerprintValidation = false;
            _expectedResumeMapModifierSnapshot = default;
        }

        _nextPendingRetryTick = 0;

        _currentAreaName = area?.Name ?? "Unknown area";
        _currentAreaKey = GetAreaKey(area);
        _trackingCurrentArea = IsTrackableArea(area);
        _hasPausedTrackableArea = false;
        _lastAreaChangeCount = GameController?.Game?.AreaChangeCount ?? _lastAreaChangeCount;
        _currentAreaStartedUtc = DateTime.UtcNow;
        _isInNonMapArea = !_trackingCurrentArea;
        _nonMapAreaStartedUtc = _isInNonMapArea ? DateTime.UtcNow : default;
        UpdateCurrentAreaMapModifierStats(captureAsStart: _trackingCurrentArea && resetStats);
        ResetDebugCounters();
    }

    private void PauseCurrentArea()
    {
        if (!_trackingCurrentArea)
            return;

        UpdateLiveTimers();
        UpdateCurrentAreaMapModifierStats();
        _pendingDropKeys.Clear();
        _trackingCurrentArea = false;
        _hasPausedTrackableArea = true;
    }

    private void ResumePausedArea(AreaInstance area, bool validateFingerprintWhenReady = false)
    {
        var expectedSnapshot = _lastKnownMapModifierSnapshot;

        _currentAreaName = area?.Name ?? _currentAreaName;
        _currentAreaKey = GetAreaKey(area);
        _trackingCurrentArea = true;
        _hasPausedTrackableArea = false;
        _pendingResumeFingerprintValidation = validateFingerprintWhenReady;
        _expectedResumeMapModifierSnapshot = validateFingerprintWhenReady ? expectedSnapshot : default;
        _lastAreaChangeCount = GameController?.Game?.AreaChangeCount ?? _lastAreaChangeCount;
        _currentAreaStartedUtc = DateTime.UtcNow - _currentAreaStats.Elapsed;
        _isInNonMapArea = false;
        _nonMapAreaStartedUtc = default;
        _nextPendingRetryTick = 0;
        UpdateCurrentAreaMapModifierStats();
        ResetDebugCounters();
    }

    private void StartNonMapAreaSegment()
    {
        if (!_isInNonMapArea)
        {
            _isInNonMapArea = true;
            _nonMapAreaStartedUtc = DateTime.UtcNow;
        }

        _sessionStats.CurrentNonMapElapsed = DateTime.UtcNow - _nonMapAreaStartedUtc;
    }

    private bool CanContinueCurrentArea(AreaInstance area)
    {
        if (!_trackingCurrentArea || !IsTrackableArea(area))
            return false;

        return HasSameMapModifierFingerprint(ReadCurrentMapModifierSnapshot());
    }

    private bool CanContinuePausedArea(AreaInstance area)
    {
        if (!_hasPausedTrackableArea || !IsTrackableArea(area))
            return false;

        return HasSameMapModifierFingerprint(ReadCurrentMapModifierSnapshot());
    }

    private bool HasSameMapModifierFingerprint(MapModifierReadResult readResult)
    {
        return _hasLastKnownMapModifierSnapshot &&
               readResult.MatchedStatsCount > 0 &&
               _lastKnownMapModifierSnapshot.Equals(readResult.Snapshot);
    }

    private bool ValidatePendingResumeFingerprintIfReady()
    {
        if (!_pendingResumeFingerprintValidation)
            return true;

        return ValidatePendingResumeFingerprint(ReadCurrentMapModifierSnapshot());
    }

    private static bool IsTrackableArea(AreaInstance area)
    {
        if (area is not { IsTown: false, IsHideout: false, IsPeaceful: false })
            return false;

        var name = area.Name ?? string.Empty;
        return !name.Contains("Vault", StringComparison.InvariantCultureIgnoreCase);
    }

    private bool IsInHideout()
    {
        return GameController?.Area?.CurrentArea?.IsHideout == true;
    }

    private static string GetAreaKey(AreaInstance area)
    {
        if (area == null)
            return string.Empty;

        var areaId = area.Area?.Id ?? string.Empty;
        var name = area.Name ?? string.Empty;
        return $"{areaId}|{name}|{area.IsTown}|{area.IsHideout}|{area.IsPeaceful}";
    }

    private void FinalizeCurrentArea()
    {
        if (!_trackingCurrentArea && !_hasPausedTrackableArea)
            return;

        UpdateLiveTimers();
        CommitFinalMapModifierStats();

        try
        {
            SaveAreaStatsToDisk();
        }
        catch (Exception ex)
        {
            LogError($"SaveAreaStatsToDisk failed: {ex}");
        }

        if (!IsSaveLockedArea(_currentAreaName))
            _trackedDropWindowStats.AddMap();

        var isFailMap = _currentAreaStats.UniqueItems < Settings.Tracking.FailUniqueThreshold;
        if (isFailMap && !IsSaveLockedArea(_currentAreaName))
            _sessionStats.AddFail();

        var shouldCountInAverage = !IsSaveLockedArea(_currentAreaName) &&
                                   (!isFailMap || Settings.Tracking.CountFailMapToStatistic) &&
                                   (_currentAreaStats.HasDrops || Settings.Tracking.IncludeEmptyAreasInAverage);
        if (shouldCountInAverage)
        {
            ApplyAreaReview(new AreaReview
            {
                AreaName = _currentAreaName,
                IsFailMap = isFailMap,
                Stats = _currentAreaStats.Clone()
            });
        }

        SaveSessionSnapshot();
        _hasPausedTrackableArea = false;
    }

    private void ResetSessionStats()
    {
        _sessionStats.Reset();
        _appliedAreaReviews.Clear();
        _currentAreaStats.Reset();
        _hasPausedTrackableArea = false;
        _hasLastKnownMapModifierSnapshot = false;
        _lastKnownMapModifierSnapshot = default;
        _pendingResumeFingerprintValidation = false;
        _expectedResumeMapModifierSnapshot = default;
        _seenPersistentItemKeys.Clear();
        _pendingDropKeys.Clear();
        _currentAreaStartedUtc = DateTime.UtcNow;
        _nonMapAreaStartedUtc = default;
        _isInNonMapArea = false;
        SaveSessionSnapshot();
        StartTrackingArea(GameController?.Area?.CurrentArea);
    }

    private void ApplyAreaReview(AreaReview review)
    {
        if (review == null)
            return;

        _sessionStats.Add(review.Stats);
        _appliedAreaReviews.Add(review);
        RefreshSessionLastT0UniqueName();
        SaveSessionSnapshot();
    }

    private void UndoLastAppliedMap()
    {
        if (_appliedAreaReviews.Count == 0)
            return;

        var lastIndex = _appliedAreaReviews.Count - 1;
        var review = _appliedAreaReviews[lastIndex];
        _appliedAreaReviews.RemoveAt(lastIndex);

        if (review.IsFailMap)
            _sessionStats.RemoveFail();

        _sessionStats.Remove(review.Stats);
        RefreshSessionLastT0UniqueName();
        SaveSessionSnapshot();
    }

    private void RefreshSessionLastT0UniqueName()
    {
        _sessionStats.LastT0UniqueName = _appliedAreaReviews
            .Select(x => x.Stats.LastT0UniqueName)
            .LastOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
    }

    private void SaveSessionSnapshotIfNeeded()
    {
        var now = Environment.TickCount64;
        if (now < Interlocked.Read(ref _nextSessionSnapshotTick))
            return;

        Interlocked.Exchange(ref _nextSessionSnapshotTick, now + SessionSnapshotIntervalMs);
        SaveSessionSnapshot();
    }

    private void SaveSessionSnapshot()
    {
        try
        {
            UpdateLiveTimers();

            var snapshot = new SessionSnapshot
            {
                SavedAtUtc = DateTime.UtcNow,
                AreasTracked = _sessionStats.AreasTracked,
                FailedAreas = _sessionStats.FailedAreas,
                LastT0UniqueName = _sessionStats.LastT0UniqueName,
                TotalUniqueItems = _sessionStats.TotalUniqueItems,
                TotalT0UniqueItems = _sessionStats.TotalT0UniqueItems,
                TotalCurrencyQuantity = _sessionStats.TotalCurrencyQuantity,
                TotalFragmentQuantity = _sessionStats.TotalFragmentQuantity,
                TotalNormalBaseItems = _sessionStats.TotalNormalBaseItems,
                TotalMagicBaseItems = _sessionStats.TotalMagicBaseItems,
                TotalRareBaseItems = _sessionStats.TotalRareBaseItems,
                TotalDivineOrbQuantity = _sessionStats.TotalDivineOrbQuantity,
                TotalValdosPuzzleBoxQuantity = _sessionStats.TotalValdosPuzzleBoxQuantity,
                TotalStartItemQuantity = _sessionStats.TotalStartItemQuantity,
                TotalFinalItemQuantity = _sessionStats.TotalFinalItemQuantity,
                TotalStartItemRarity = _sessionStats.TotalStartItemRarity,
                TotalFinalItemRarity = _sessionStats.TotalFinalItemRarity,
                TotalFinalPackSize = _sessionStats.TotalFinalPackSize,
                TotalFinalMoreCurrency = _sessionStats.TotalFinalMoreCurrency,
                TotalFinalMoreMaps = _sessionStats.TotalFinalMoreMaps,
                TotalFinalMoreScarabs = _sessionStats.TotalFinalMoreScarabs,
                TotalMapTimeTicks = _sessionStats.TotalMapTime.Ticks,
                TotalNonMapTimeTicks = (_sessionStats.TotalNonMapTime + _sessionStats.CurrentNonMapElapsed).Ticks,
                CustomTrackedTotals = new Dictionary<string, int>(_sessionStats.CustomTrackedTotals, StringComparer.InvariantCultureIgnoreCase),
                TrackedDropWindow = new TrackedDropWindowSnapshot
                {
                    TotalMaps = _trackedDropWindowStats.TotalMaps,
                    SessionMaps = _trackedDropWindowStats.SessionMaps,
                    TotalCounts = new Dictionary<string, int>(_trackedDropWindowStats.TotalCounts, StringComparer.InvariantCultureIgnoreCase),
                    SessionCounts = new Dictionary<string, int>(_trackedDropWindowStats.SessionCounts, StringComparer.InvariantCultureIgnoreCase)
                },
                AppliedAreaReviews = _appliedAreaReviews
                    .Select(x => AreaReviewSnapshot.FromReview(x))
                    .ToList()
            };

            var path = GetSessionSnapshotPath();
            EnsureParentDirectoryExists(path);
            File.WriteAllText(path, JsonSerializer.Serialize(snapshot, JsonOptions));
        }
        catch (Exception ex)
        {
            LogError($"SaveSessionSnapshot failed: {ex}");
        }
    }

    private void LoadLastSessionSnapshot()
    {
        try
        {
            var path = GetSessionSnapshotPath();
            if (!File.Exists(path))
                return;

            var snapshot = JsonSerializer.Deserialize<SessionSnapshot>(File.ReadAllText(path));
            if (snapshot == null)
                return;

            _appliedAreaReviews.Clear();
            foreach (var review in snapshot.AppliedAreaReviews ?? [])
                _appliedAreaReviews.Add(review.ToReview());

            _sessionStats.Load(snapshot);
            _trackedDropWindowStats.Load(snapshot.TrackedDropWindow ?? new TrackedDropWindowSnapshot());
            RefreshSessionLastT0UniqueName();
        }
        catch (Exception ex)
        {
            LogError($"LoadLastSessionSnapshot failed: {ex}");
        }
    }

    private string GetSessionSnapshotPath()
    {
        var sourcePath = Path.Combine(Environment.CurrentDirectory, "Plugins", "Source", "MapDropStatistics", SessionSnapshotFileName);
        if (Directory.Exists(Path.GetDirectoryName(sourcePath)!))
            return sourcePath;

        return Path.Combine(DirectoryFullName, SessionSnapshotFileName);
    }

    private void FinalizeNonMapSegment()
    {
        UpdateLiveTimers();

        if (_isInNonMapArea && _nonMapAreaStartedUtc != default)
        {
            _sessionStats.AddNonMapTime(DateTime.UtcNow - _nonMapAreaStartedUtc);
            _sessionStats.CurrentNonMapElapsed = TimeSpan.Zero;
            _isInNonMapArea = false;
            _nonMapAreaStartedUtc = default;
            SaveSessionSnapshot();
        }
    }

    private void UpdateLiveTimers()
    {
        var now = DateTime.UtcNow;

        if (_trackingCurrentArea && _currentAreaStartedUtc != default)
            _currentAreaStats.Elapsed = now - _currentAreaStartedUtc;

        if (_isInNonMapArea && _nonMapAreaStartedUtc != default)
            _sessionStats.CurrentNonMapElapsed = now - _nonMapAreaStartedUtc;
        else
            _sessionStats.CurrentNonMapElapsed = TimeSpan.Zero;
    }

    private void UpdateCurrentAreaMapModifierStats(bool captureAsStart = false)
    {
        if (!_trackingCurrentArea)
            return;

        var readResult = ReadCurrentMapModifierSnapshot();
        if (!ValidatePendingResumeFingerprint(readResult))
            return;

        if (readResult.MatchedStatsCount > 0)
        {
            _lastKnownMapModifierSnapshot = readResult.Snapshot;
            _hasLastKnownMapModifierSnapshot = true;
        }

        var snapshot = readResult.MatchedStatsCount > 0
            ? readResult.Snapshot
            : _hasLastKnownMapModifierSnapshot
                ? _lastKnownMapModifierSnapshot
                : default;

        _currentAreaStats.CurrentItemQuantity = snapshot.ItemQuantity;
        _currentAreaStats.CurrentItemRarity = snapshot.ItemRarity;
        _currentAreaStats.CurrentPackSize = snapshot.PackSize;
        _currentAreaStats.CurrentMoreCurrency = snapshot.MoreCurrency;
        _currentAreaStats.CurrentMoreMaps = snapshot.MoreMaps;
        _currentAreaStats.CurrentMoreScarabs = snapshot.MoreScarabs;

        if (!captureAsStart)
            return;

        _currentAreaStats.StartItemQuantity = snapshot.ItemQuantity;
        _currentAreaStats.FinalItemQuantity = snapshot.ItemQuantity;
        _currentAreaStats.StartItemRarity = snapshot.ItemRarity;
        _currentAreaStats.FinalItemRarity = snapshot.ItemRarity;
        _currentAreaStats.FinalPackSize = snapshot.PackSize;
        _currentAreaStats.FinalMoreCurrency = snapshot.MoreCurrency;
        _currentAreaStats.FinalMoreMaps = snapshot.MoreMaps;
        _currentAreaStats.FinalMoreScarabs = snapshot.MoreScarabs;
    }

    private bool ValidatePendingResumeFingerprint(MapModifierReadResult readResult)
    {
        if (!_pendingResumeFingerprintValidation)
            return true;

        if (readResult.MatchedStatsCount == 0)
            return false;

        _pendingResumeFingerprintValidation = false;
        var expectedSnapshot = _expectedResumeMapModifierSnapshot;
        _expectedResumeMapModifierSnapshot = default;

        if (expectedSnapshot.Equals(readResult.Snapshot))
            return true;

        FinalizeCurrentArea();
        StartTrackingArea(GameController?.Area?.CurrentArea, resetStats: true);
        return false;
    }

    private void CommitFinalMapModifierStats()
    {
        _currentAreaStats.FinalItemQuantity = _currentAreaStats.CurrentItemQuantity;
        _currentAreaStats.FinalItemRarity = _currentAreaStats.CurrentItemRarity;
        _currentAreaStats.FinalPackSize = _currentAreaStats.CurrentPackSize;
        _currentAreaStats.FinalMoreCurrency = _currentAreaStats.CurrentMoreCurrency;
        _currentAreaStats.FinalMoreMaps = _currentAreaStats.CurrentMoreMaps;
        _currentAreaStats.FinalMoreScarabs = _currentAreaStats.CurrentMoreScarabs;
    }

    private MapModifierReadResult ReadCurrentMapModifierSnapshot()
    {
        var mapStats = GameController?.IngameState?.Data?.MapStats;
        if (mapStats == null)
            return default;

        var itemQuantity = 0;
        var itemRarity = 0;
        var packSize = 0;
        var moreCurrency = 0;
        var moreMaps = 0;
        var moreScarabs = 0;
        var matchedStatsCount = 0;

        foreach (var instanceStat in mapStats)
        {
            var key = NormalizeMapStatKey(instanceStat.Key.ToString());
            var value = instanceStat.Value;
            if (value == 0 || string.IsNullOrWhiteSpace(key))
                continue;

            if (IsNoisyMapStat(key))
                continue;

            if (IsMapQuantityStat(key))
            {
                itemQuantity += value;
                matchedStatsCount++;
            }
            else if (IsMapRarityStat(key))
            {
                itemRarity += value;
                matchedStatsCount++;
            }
            else if (IsPackSizeStat(key))
            {
                packSize += value;
                matchedStatsCount++;
            }
            else if (IsMoreCurrencyStat(key))
            {
                moreCurrency += value;
                matchedStatsCount++;
            }
            else if (IsMoreMapsStat(key))
            {
                moreMaps += value;
                matchedStatsCount++;
            }
            else if (IsMoreScarabsStat(key))
            {
                moreScarabs += value;
                matchedStatsCount++;
            }
        }

        return new MapModifierReadResult(
            new MapModifierSnapshot(itemQuantity, itemRarity, packSize, moreCurrency, moreMaps, moreScarabs),
            matchedStatsCount);
    }

    private static string NormalizeMapStatKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private static bool IsNoisyMapStat(string normalizedKey)
    {
        return MapStatNoiseTokens.Any(normalizedKey.Contains);
    }

    private static bool IsMapQuantityStat(string normalizedKey)
    {
        return normalizedKey.Contains("quantity") &&
               normalizedKey.Contains("item") &&
               (normalizedKey.StartsWith("map", StringComparison.Ordinal) || normalizedKey.Contains("area"));
    }

    private static bool IsMapRarityStat(string normalizedKey)
    {
        return normalizedKey.Contains("rarity") &&
               normalizedKey.Contains("item") &&
               (normalizedKey.StartsWith("map", StringComparison.Ordinal) || normalizedKey.Contains("area"));
    }

    private static bool IsPackSizeStat(string normalizedKey)
    {
        return normalizedKey.Contains("packsize") &&
               (normalizedKey.StartsWith("map", StringComparison.Ordinal) || normalizedKey.Contains("area"));
    }

    private static bool IsMoreCurrencyStat(string normalizedKey)
    {
        if (normalizedKey.Contains("currencydropchance"))
            return true;

        return normalizedKey.Contains("currency") &&
               normalizedKey.Contains("found") &&
               normalizedKey.Contains("area") &&
               (normalizedKey.Contains("more") || normalizedKey.StartsWith("map", StringComparison.Ordinal));
    }

    private static bool IsMoreMapsStat(string normalizedKey)
    {
        if ((normalizedKey.Contains("map") || normalizedKey.Contains("maps")) &&
            normalizedKey.Contains("dropchance") &&
            !normalizedKey.Contains("currency") &&
            !normalizedKey.Contains("scarab"))
        {
            return true;
        }

        return normalizedKey.Contains("maps") &&
               normalizedKey.Contains("found") &&
               normalizedKey.Contains("area") &&
               (normalizedKey.Contains("more") || normalizedKey.StartsWith("map", StringComparison.Ordinal));
    }

    private static bool IsMoreScarabsStat(string normalizedKey)
    {
        if (normalizedKey.Contains("scarabdropchance") || normalizedKey.Contains("scarabsdropchance"))
            return true;

        return normalizedKey.Contains("scarab") &&
               normalizedKey.Contains("found") &&
               normalizedKey.Contains("area") &&
               (normalizedKey.Contains("more") || normalizedKey.StartsWith("map", StringComparison.Ordinal));
    }

    private static string GetPersistentItemKey(Entity worldEntity, Entity itemEntity)
    {
        if (worldEntity?.Id <= 0 || itemEntity?.Path == null)
            return string.Empty;

        var grid = worldEntity.GridPosNum;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{worldEntity.Id}:{itemEntity.Path}:{(int)MathF.Round(grid.X)}:{(int)MathF.Round(grid.Y)}");
    }

    private static long GetDropKey(Entity worldEntity, Entity itemEntity)
    {
        if (worldEntity?.Address > 0)
            return worldEntity.Address;

        if (itemEntity?.Address > 0)
            return itemEntity.Address;

        return 0;
    }

    private bool HasPendingProvisionalCount(long dropKey, string category, string baseName)
    {
        if (!_pendingDropKeys.TryGetValue(dropKey, out var pending))
            return false;

        return pending.ProvisionalStackQuantity > 0 &&
               string.Equals(pending.ProvisionalStackCategory, category, StringComparison.Ordinal) &&
               string.Equals(pending.BaseName, baseName, StringComparison.InvariantCultureIgnoreCase);
    }

    private void SavePending(long dropKey, string persistentItemKey, Entity worldEntity, Entity itemEntity, string reason, string provisionalStackCategory = "", int provisionalStackQuantity = 0, string baseName = "")
    {
        _pendingDropKeys[dropKey] = new PendingDropInfo
        {
            DropKey = dropKey,
            PersistentItemKey = persistentItemKey,
            WorldEntity = worldEntity,
            ItemEntity = itemEntity,
            Reason = reason,
            Path = itemEntity?.Path ?? string.Empty,
            BaseName = baseName,
            ProvisionalStackCategory = provisionalStackCategory,
            ProvisionalStackQuantity = provisionalStackQuantity,
            LastSeenUtc = DateTime.UtcNow
        };

    }

    private string GetUniqueDisplayName(Entity itemEntity, BaseItemType baseItemType, Mods mods)
    {
        if (!string.IsNullOrWhiteSpace(mods.UniqueName))
            return mods.UniqueName;

        if (!mods.Identified && mods.ItemRarity == ItemRarity.Unique)
        {
            var artPath = itemEntity.GetComponent<RenderItem>()?.ResourcePath;
            if (!string.IsNullOrWhiteSpace(artPath) && _uniqueArtMapping.TryGetValue(artPath, out var candidates))
            {
                var filteredCandidates = candidates
                    .Where(x => !x.StartsWith("Replica ", StringComparison.Ordinal) || x.StartsWith("Replica Dragonfang's Flight", StringComparison.Ordinal))
                    .Distinct(StringComparer.InvariantCultureIgnoreCase)
                    .ToList();

                if (filteredCandidates.Count == 1)
                    return filteredCandidates[0];

                var t0Candidate = filteredCandidates.FirstOrDefault(IsT0Unique);
                if (!string.IsNullOrWhiteSpace(t0Candidate))
                    return t0Candidate;
            }
        }

        return $"{baseItemType.BaseName} (unidentified)";
    }

    private bool IsT0Unique(string uniqueName)
    {
        return _t0UniqueNames.Contains(uniqueName) ||
               _t0UniqueNames.Any(t0 => uniqueName.Contains(t0, StringComparison.InvariantCultureIgnoreCase));
    }

    private HashSet<string> LoadT0UniqueNames()
    {
        var path = ResolveT0UniqueFilePath();
        if (!File.Exists(path))
        {
            EnsureParentDirectoryExists(path);
            File.WriteAllLines(path,
            [
                "# One unique name per line.",
                "# Add your full T0 list here.",
                "Mageblood",
                "Marohi Erqi"
            ]);
        }

        return File.ReadAllLines(path)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x) && !x.StartsWith("#", StringComparison.Ordinal))
            .ToHashSet(StringComparer.InvariantCultureIgnoreCase);
    }

    private string ResolveT0UniqueFilePath()
    {
        var runtimePath = Path.Combine(DirectoryFullName, T0UniqueFileName);
        var sourcePath = Path.Combine(Environment.CurrentDirectory, "Plugins", "Source", "MapDropStatistics", T0UniqueFileName);

        if (File.Exists(sourcePath))
            return sourcePath;

        return runtimePath;
    }

    private Dictionary<string, List<string>> LoadUniqueArtMapping()
    {
        try
        {
            if ((GameController?.Files?.UniqueItemDescriptions?.EntriesList?.Count ?? 0) == 0 ||
                (GameController?.Files?.ItemVisualIdentities?.EntriesList?.Count ?? 0) == 0)
            {
                GameController?.Files?.LoadFiles();
            }

            if (GameController?.Files?.UniqueItemDescriptions?.EntriesList?.Count > 0 &&
                GameController?.Files?.ItemVisualIdentities?.EntriesList?.Count > 0)
            {
                var gameMapping = GameController.Files.ItemVisualIdentities.EntriesList
                    .Where(x => x.ArtPath != null)
                    .GroupJoin(
                        GameController.Files.UniqueItemDescriptions.EntriesList.Where(x => x.ItemVisualIdentity != null),
                        x => x,
                        x => x.ItemVisualIdentity,
                        (visualIdentity, descriptions) => (visualIdentity.ArtPath, Descriptions: descriptions.ToList()))
                    .GroupBy(x => x.ArtPath, x => x.Descriptions)
                    .Select(x => new
                    {
                        x.Key,
                        Names = x.SelectMany(items => items)
                            .Select(item => item.UniqueName?.Text)
                            .Where(name => !string.IsNullOrWhiteSpace(name))
                            .Distinct(StringComparer.InvariantCultureIgnoreCase)
                            .ToList()
                    })
                    .Where(x => x.Names.Count > 0)
                    .ToDictionary(x => x.Key, x => x.Names, StringComparer.InvariantCultureIgnoreCase);

                if (gameMapping.Count > 0)
                    return gameMapping;
            }
        }
        catch (Exception ex)
        {
            LogError($"LoadUniqueArtMapping(game files) failed: {ex}");
        }

        try
        {
            var fallbackPath = Path.Combine(DirectoryFullName, DefaultUniqueArtMappingFileName);
            if (!File.Exists(fallbackPath))
                return new Dictionary<string, List<string>>(StringComparer.InvariantCultureIgnoreCase);

            var json = File.ReadAllText(fallbackPath);
            var mapping = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json);
            return mapping != null
                ? new Dictionary<string, List<string>>(mapping, StringComparer.InvariantCultureIgnoreCase)
                : new Dictionary<string, List<string>>(StringComparer.InvariantCultureIgnoreCase);
        }
        catch (Exception ex)
        {
            LogError($"LoadUniqueArtMapping(fallback file) failed: {ex}");
            return new Dictionary<string, List<string>>(StringComparer.InvariantCultureIgnoreCase);
        }
    }

    private void ClearSavedMapStats()
    {
        try
        {
            var root = GetSavedMapStatsDirectory();
            if (Directory.Exists(root))
                Directory.Delete(root, true);

            Directory.CreateDirectory(root);
            RefreshSavedMapStats();
        }
        catch (Exception ex)
        {
            LogError($"ClearSavedMapStats failed: {ex}");
        }
    }

    private void SaveAreaStatsToDisk()
    {
        if (!Settings.Actions.SaveMapStats && !IsSaveLockedArea(_currentAreaName))
            return;

        PrunePendingDrops();

        var payload = new AreaDump
        {
            AreaName = _currentAreaName,
            AreaKey = _currentAreaKey,
            SavedAtUtc = DateTime.UtcNow,
            UniqueItems = _currentAreaStats.UniqueItems,
            T0UniqueItems = _currentAreaStats.T0UniqueItems,
            CurrencyStacks = _currentAreaStats.CurrencyStacks,
            CurrencyQuantity = _currentAreaStats.CurrencyQuantity,
            FragmentStacks = _currentAreaStats.FragmentStacks,
            FragmentQuantity = _currentAreaStats.FragmentQuantity,
            NormalBaseItems = _currentAreaStats.NormalBaseItems,
            MagicBaseItems = _currentAreaStats.MagicBaseItems,
            RareBaseItems = _currentAreaStats.RareBaseItems,
            DivineOrbQuantity = _currentAreaStats.DivineOrbQuantity,
            ValdosPuzzleBoxQuantity = _currentAreaStats.ValdosPuzzleBoxQuantity,
            StartItemQuantity = _currentAreaStats.StartItemQuantity,
            FinalItemQuantity = _currentAreaStats.FinalItemQuantity,
            StartItemRarity = _currentAreaStats.StartItemRarity,
            FinalItemRarity = _currentAreaStats.FinalItemRarity,
            FinalPackSize = _currentAreaStats.FinalPackSize,
            FinalMoreCurrency = _currentAreaStats.FinalMoreCurrency,
            FinalMoreMaps = _currentAreaStats.FinalMoreMaps,
            FinalMoreScarabs = _currentAreaStats.FinalMoreScarabs,
            CustomTracked = _currentAreaStats.CustomTrackedCounts
                .OrderBy(x => x.Key, StringComparer.InvariantCultureIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Value, StringComparer.InvariantCultureIgnoreCase),
            Drops = _currentAreaStats.DropCounts
                .OrderBy(x => x.Key, StringComparer.InvariantCultureIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Value, StringComparer.InvariantCultureIgnoreCase),
            Pending = _pendingDropKeys
                .OrderBy(x => x.Key)
                .ToDictionary(
                    x => x.Key.ToString(CultureInfo.InvariantCulture),
                    x => new PendingDumpInfo
                    {
                        DropKey = x.Value.DropKey,
                        PersistentItemKey = x.Value.PersistentItemKey,
                        Reason = x.Value.Reason,
                        Path = x.Value.Path,
                        BaseName = x.Value.BaseName,
                        ProvisionalStackCategory = x.Value.ProvisionalStackCategory,
                        ProvisionalStackQuantity = x.Value.ProvisionalStackQuantity,
                        LastSeenUtc = x.Value.LastSeenUtc
                    },
                    StringComparer.InvariantCultureIgnoreCase)
        };

        var safeAreaName = string.Concat((_currentAreaName ?? "Unknown area").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var fileName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{safeAreaName}.json";
        var path = Path.Combine(GetSavedMapStatsDirectory(), fileName);
        EnsureParentDirectoryExists(path);
        File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOptions));
        PruneSavedMapStats();
        RefreshSavedMapStats();
    }

    private string GetSavedMapStatsDirectory()
    {
        var root = Path.Combine(DirectoryFullName, "SavedMapStats");
        Directory.CreateDirectory(root);
        return root;
    }

    private void PruneSavedMapStats()
    {
        var directory = GetSavedMapStatsDirectory();
        var files = new DirectoryInfo(directory)
            .GetFiles("*.json")
            .OrderByDescending(x => x.CreationTimeUtc)
            .ToList();

        foreach (var file in files.Skip(Math.Max(Settings.Actions.MaxSavedMapStats, 1)))
            file.Delete();
    }

    private void RefreshSavedMapStats()
    {
        _savedMapStatFiles.Clear();
        var directory = GetSavedMapStatsDirectory();

        _savedMapStatFiles.AddRange(new DirectoryInfo(directory)
            .GetFiles("*.json")
            .OrderByDescending(x => x.CreationTimeUtc)
            .Select(x => x.FullName));

        if (!string.IsNullOrWhiteSpace(_selectedSavedMapStatPath) && !_savedMapStatFiles.Contains(_selectedSavedMapStatPath, StringComparer.OrdinalIgnoreCase))
        {
            _selectedSavedMapStatPath = string.Empty;
            _selectedAreaDump = null;
        }
    }

    private void LoadSavedMapStat(string filePath)
    {
        try
        {
            _selectedSavedMapStatPath = filePath ?? string.Empty;
            _selectedAreaDump = string.IsNullOrWhiteSpace(filePath)
                ? null
                : JsonSerializer.Deserialize<AreaDump>(File.ReadAllText(filePath), JsonOptions);
        }
        catch (Exception ex)
        {
            _selectedAreaDump = null;
            LogError($"LoadSavedMapStat failed: {ex}");
        }
    }

    private static void EnsureParentDirectoryExists(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    private void PrunePendingDrops()
    {
        if (_pendingDropKeys.Count == 0)
            return;

        var nowUtc = DateTime.UtcNow;
        foreach (var pending in _pendingDropKeys.Values.ToList())
        {
            if ((nowUtc - pending.LastSeenUtc).TotalMilliseconds > PendingEntryTtlMs ||
                pending.WorldEntity?.IsValid != true ||
                pending.ItemEntity?.IsValid != true ||
                _seenPersistentItemKeys.Contains(pending.PersistentItemKey))
            {
                _pendingDropKeys.Remove(pending.DropKey);
            }
        }
    }

}
