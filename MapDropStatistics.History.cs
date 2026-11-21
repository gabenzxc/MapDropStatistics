using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using ExileCore.Shared.Enums;

namespace MapDropStatistics;

public partial class MapDropStatistics
{
    private const int PickupPollMs = 150;
    private const int PickupEvidenceMs = 1200;
    private const float PickupRangeSquared = 32 * 32;
    private const float PartyObservationRangeSquared = 60 * 60;
    private MapHistory _history;
    private DashboardServer _dashboard;
    private MapRun _activeRun;
    private uint _mapHash;
    private int _mapInstanceId;
    private long _nextPickupPoll;
    private long _nextRunSave;
    private long _nextPriceRead;
    private long _nextModifierRead;
    private bool _localWasDead;
    private int? _lastGoldBalance;
    private readonly Dictionary<GameStat, MapModifier> _lastRunModifiers = [];
    private readonly Dictionary<LootKey, MapLoot> _runLoot = [];
    private readonly Dictionary<uint, GroundDrop> _groundDrops = [];
    private readonly HashSet<uint> _resolvedGroundDrops = [];
    private readonly List<PickupCandidate> _pickupCandidates = [];
    private readonly Dictionary<LootKey, InventoryGain> _inventoryGains = [];
    private Dictionary<LootKey, int> _inventoryCounts;
    private List<PartyObservation> _partyInArea = [];
    private readonly HashSet<string> _deadPartyMembers = new(StringComparer.OrdinalIgnoreCase);
    private MarketPriceArchive _priceArchive;
    private volatile PricingSourceStatus _pricingStatus;
    private long _nextPricingStatusRead;
    private MarketPriceSnapshot _market;
    private Task<MarketPriceSnapshot> _marketRead;
    private Task<MarketPriceSnapshot> _scheduledMarketRead;
    private string _scheduledMarketLeague;
    private long _nextMarketCapture;

    private sealed record LootKey(string Path, string Name, string Category, int? MapTier = null, string UniqueType = null);
    private sealed record GroundDrop(LootKey Key, int Quantity, Vector2 Position, long Address);
    private sealed record PickupCandidate(uint Id, GroundDrop Drop, long Seen, string NearbyPartyMember);
    private sealed record PartyObservation(string Name, Vector2 Position, bool Dead);
    private sealed record InventoryGain(int Count, long Seen);

    private void InitialiseDashboard()
    {
        try
        {
            var root = string.IsNullOrWhiteSpace(ConfigDirectory)
                ? Path.Combine(Environment.CurrentDirectory, "config", "MapDropStatistics")
                : ConfigDirectory;
            var uniqueTypes = GetArchivedUniqueTypes();
            var beltNames = uniqueTypes.Where(entry => entry.Value == "Belts").Select(entry => entry.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _history = new MapHistory(Path.Combine(root, "MapHistory"), message => LogError(message), beltNames,
                IsT0Unique, GetWidgetDefaults(), uniqueTypes);
            _priceArchive = new MarketPriceArchive(Path.Combine(root, "MarketPrices"), GetPricerCacheRoot,
                message => LogError(message));
            UpdatePricingStatus();
            ImportLegacyMapStats();
            FillSavedMarketPrices();
            Input.RegisterKey(System.Windows.Forms.Keys.LButton);
            UpdateDashboard();
        }
        catch (Exception ex)
        {
            LogError($"Map history could not initialize: {ex}");
        }
    }

    private Dictionary<string, string> GetArchivedUniqueTypes()
    {
        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void AddType(string name, string type)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type))
                return;
            name = name.Trim();
            if (!types.TryAdd(name, type) && types[name] != type)
                types[name] = null;
        }

        foreach (var (path, names) in _uniqueArtMapping)
        {
            var type = UniqueItemCatalog.ItemType(path);
            foreach (var name in names)
                AddType(name, type);
        }
        if (GameController?.Files?.BaseItemTypes?.Contents is { } bases)
        {
            foreach (var item in bases.Values)
            {
                if (!string.IsNullOrWhiteSpace(item.BaseName))
                    AddType($"{item.BaseName} (unidentified)", UniqueItemCatalog.ItemType(item.Metadata, item.ClassName));
            }
        }
        return types;
    }

    private void ImportLegacyMapStats()
    {
        var trackedMaps = _history.List().Where(map => map.State != "legacy").ToArray();
        foreach (var path in _savedMapStatFiles)
        {
            var id = MapHistory.LegacyId(Path.GetFileName(path));
            try
            {
                var legacy = JsonSerializer.Deserialize<AreaDump>(File.ReadAllText(path));
                if (legacy == null)
                    continue;
                var totals = new MapDropTotals(legacy.CurrencyQuantity, legacy.UniqueItems,
                    legacy.NormalBaseItems, legacy.MagicBaseItems, legacy.RareBaseItems);
                var matching = trackedMaps.Where(map => map.Name == legacy.AreaName &&
                    map.Quantity == legacy.FinalItemQuantity && map.Rarity == legacy.FinalItemRarity &&
                    map.PackSize == legacy.FinalPackSize && Math.Abs((map.UpdatedUtc - legacy.SavedAtUtc).TotalSeconds) < 3)
                    .ToArray();
                if (matching.Length == 1)
                    _history.FillSavedCounters(matching[0].Id, totals, legacy.FinalMoreCurrency, legacy.FinalMoreMaps,
                        legacy.FinalMoreScarabs, legacy.T0UniqueItems);
                if (_history.GetSummary(id) != null)
                {
                    _history.FillSavedCounters(id, totals, legacy.FinalMoreCurrency, legacy.FinalMoreMaps,
                        legacy.FinalMoreScarabs, legacy.T0UniqueItems);
                    continue;
                }
                var run = new MapRun
                {
                    Id = id, Name = legacy.AreaName, State = "legacy",
                    StartedUtc = legacy.SavedAtUtc, UpdatedUtc = legacy.SavedAtUtc,
                    Quantity = legacy.FinalItemQuantity, Rarity = legacy.FinalItemRarity, PackSize = legacy.FinalPackSize,
                    MoreCurrency = legacy.FinalMoreCurrency, MoreMaps = legacy.FinalMoreMaps, MoreScarabs = legacy.FinalMoreScarabs,
                    DropTotals = totals, T0UniquesDropped = legacy.T0UniqueItems
                };
                foreach (var (label, quantity) in legacy.Drops ?? [])
                {
                    var parts = label.Split("::", 2, StringSplitOptions.None);
                    run.Loot.Add(new MapLoot(Guid.NewGuid(), parts[^1], parts.Length == 2 ? parts[0] : "Item")
                    {
                        Dropped = quantity
                    });
                }
                foreach (var (name, value) in new Dictionary<string, int>
                {
                    ["More Currency"] = legacy.FinalMoreCurrency,
                    ["More Maps"] = legacy.FinalMoreMaps,
                    ["More Scarabs"] = legacy.FinalMoreScarabs
                })
                {
                    if (value != 0)
                        run.Modifiers.Add(new MapModifier(name, value, null));
                }
                _history.Save(run);
            }
            catch (Exception ex)
            {
                LogError($"Could not import {Path.GetFileName(path)}: {ex.Message}");
            }
        }
    }

    private void FillSavedMarketPrices()
    {
        var archive = _priceArchive;
        var history = _history;
        _ = Task.Run(() =>
        {
            try
            {
                history.FillMarketPrices(archive, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                LogError($"Could not fill saved loot prices: {ex}");
            }
        });
    }

    private void UpdateDashboard()
    {
        if (_history == null)
            return;
        var port = Settings.Dashboard.ServeLocally && Settings.Enable ? Settings.Dashboard.Port.Value : 0;
        if (_dashboard?.Port == port || (_dashboard == null && port == 0))
            return;
        _dashboard?.Dispose();
        var pricerCacheRoot = GetPricerCacheRoot();
        _dashboard = port == 0 ? null : new DashboardServer(port, _history, message => LogError(message),
            new NinjaItemIcons(pricerCacheRoot), () => _pricingStatus);
    }

    private void OpenDashboard()
    {
        if (_dashboard?.IsListening == false)
        {
            _dashboard.Dispose();
            _dashboard = null;
        }
        UpdateDashboard();
        if (_dashboard?.IsListening != true)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(_dashboard.Url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogError($"Could not open the dashboard: {ex.Message}");
        }
    }

    public override void OnClose()
    {
        CloseDashboard();
        base.OnClose();
    }

    public override void OnPluginDestroyForHotReload()
    {
        CloseDashboard();
        base.OnPluginDestroyForHotReload();
    }

    private void CloseDashboard()
    {
        _history?.ObserveHideout(false);
        if (_activeRun?.State is "active" or "paused")
            SaveRunCheckpoint(_activeRun.State == "paused" ? "recorded" : "interrupted");
        if (_history != null)
            SaveSessionSnapshot();
        _dashboard?.Dispose();
        _dashboard = null;
    }

    private bool IsSameMapInstance(AreaInstance area, bool allowUnknown = false)
    {
        if (area == null || !string.Equals(_currentAreaKey, GetAreaKey(area), StringComparison.Ordinal))
            return false;
        var hash = area.Hash;
        if (_mapHash != 0 && hash != 0)
            return _mapHash == hash;
        var instance = area.InstanceId;
        return _mapInstanceId != 0 && instance != 0 ? _mapInstanceId == instance : allowUnknown;
    }

    private void StartRun(AreaInstance area)
    {
        _mapHash = area?.Hash ?? 0;
        _mapInstanceId = area?.InstanceId ?? 0;
        _activeRun = _trackingCurrentArea && _history != null
            ? new MapRun
            {
                Name = _currentAreaName, StartedUtc = DateTime.UtcNow, Altars = [], SessionId = _history.ActiveSessionId,
                TracksUniqueBelts = true,
                Pricing = new MapPricing(_history.DefaultMapPriceDivine, SnapshotWindowHours: MarketPriceArchive.WindowHours)
            }
            : null;
        _seenAltars.Clear();
        _recordedEncounterSignals.Clear();
        _nextEncounterPanelRead = 0;
        MarkEncounterArea(area, startsRun: true);
        _runLoot.Clear();
        _groundDrops.Clear();
        _resolvedGroundDrops.Clear();
        _deadPartyMembers.Clear();
        _lastRunModifiers.Clear();
        _localWasDead = false;
        _market = null;
        _marketRead = null;
        _nextPriceRead = 0;
        _nextModifierRead = 0;
        ResetPickupObservations();
    }

    private void PauseRun()
    {
        SaveRunCheckpoint("paused");
        ResetPickupObservations();
    }

    private void ResumeRun(AreaInstance area)
    {
        UpdateMapIdentity(area);
        if (_activeRun == null)
            return;
        _activeRun.State = "active";
        _nextModifierRead = 0;
        ResetPickupObservations();
    }

    private void ResetPickupObservations()
    {
        _inventoryCounts = null;
        _lastGoldBalance = null;
        _inventoryGains.Clear();
        _pickupCandidates.Clear();
        _partyInArea.Clear();
        _nextPickupPoll = 0;
        _nextRunSave = 0;
        ResetAltarObservations();
    }

    private void UpdateRunTracking()
    {
        UpdateMapIdentity(GameController.Area.CurrentArea);
        if (_activeRun == null)
            return;

        var now = Environment.TickCount64;
        UpdateEncounterTracking(now);
        if (now >= _nextModifierRead)
        {
            _nextModifierRead = now + 1000;
            CaptureRunModifiers();
        }
        if (now >= _nextPickupPoll)
        {
            _nextPickupPoll = now + PickupPollMs;
            ObserveGold();
            ObserveParty();
            MatchPickups(now);
        }

        RefreshMarket(now);
        if (now >= _nextRunSave)
        {
            _nextRunSave = now + 4000;
            SaveRunCheckpoint();
        }
    }

    private void UpdateMapIdentity(AreaInstance area)
    {
        if (_mapHash == 0)
            _mapHash = area?.Hash ?? 0;
        if (_mapInstanceId == 0)
            _mapInstanceId = area?.InstanceId ?? 0;
    }

    private void CaptureRunModifiers()
    {
        var data = GameController.IngameState.Data;
        var visible = data.MapStatsVisible;
        var raw = data.MapStats;
        var stats = raw == null ? new Dictionary<GameStat, int>() : new Dictionary<GameStat, int>(raw);
        if (visible != null)
        {
            foreach (var (stat, value) in visible)
            {
                if (value != 0 && stats.GetValueOrDefault(stat) == 0)
                    stats[stat] = value;
            }
        }

        // The visible table can shrink to a single league flag while the raw table still has area mods.
        var trustVisibility = visible?.Count > 1 && (raw == null || visible.Count < raw.Count);
        var changed = false;
        var translations = GameController.Files;
        foreach (var (stat, value) in stats)
        {
            if (value == 0)
                continue;

            var previous = _lastRunModifiers.GetValueOrDefault(stat);
            var show = previous?.Visible == true || (trustVisibility && visible.GetValueOrDefault(stat) != 0);
            if (previous?.Value == value)
            {
                if (previous.Visible != show)
                {
                    _lastRunModifiers[stat] = previous with { Visible = show };
                    changed = true;
                }
                continue;
            }

            var name = stat.ToString();
            var zone = MapSummary.ZoneFromMapStat(name);
            if (zone != null && value > 0)
                MarkEncounterZone(zone);
            if (zone != null || IsEncounterSignal(name))
                RecordEncounterSignal("Map stat", $"{name} = {value}");
            var values = new Dictionary<GameStat, int> { [stat] = value };
            var text = translations?.MapStatDescriptions?.TranslateMod(values);
            if (string.IsNullOrWhiteSpace(text) || text.StartsWith('<'))
                text = translations?.StatDescriptions?.TranslateMod(values);
            _lastRunModifiers[stat] = new MapModifier(name, value,
                string.IsNullOrWhiteSpace(text) || text.StartsWith('<') ? null : text, show);
            changed = true;
        }
        if (changed)
            _activeRun.Modifiers = _lastRunModifiers.Values.OrderBy(mod => mod.Stat, StringComparer.Ordinal).ToList();
    }

    private void ObserveGold()
    {
        if (GameController.IngameState.ServerData is not { Address: > 0 } server || GameController.Player?.IsValid != true)
            return;
        var balance = server.Gold;
        if (balance < 0)
            return;
        _activeRun.GoldCollected ??= 0;
        if (_lastGoldBalance is { } previous && balance > previous)
            _activeRun.GoldCollected += (long)balance - previous;
        _lastGoldBalance = balance;
    }

    private void ObserveParty()
    {
        var player = GameController.Player;
        var localName = player?.GetComponent<Player>()?.PlayerName;
        _partyInArea.Clear();
        if (player?.IsValid != true || string.IsNullOrEmpty(localName))
            return;

        var dead = player.IsDead;
        if (dead && !_localWasDead)
            _activeRun.Deaths++;
        _localWasDead = dead;
        if (GameController.IngameState.ServerData?.PartyMembers is not { } members)
            return;
        var partyNames = members.Select(member => member.PlayerInfo?.CharacterName)
            .Where(name => !string.IsNullOrWhiteSpace(name) && !string.Equals(name, localName, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (partyNames.Count > 0 && GameController.EntityListWrapper.ValidEntitiesByType.TryGetValue(EntityType.Player, out var players))
        {
            foreach (var other in players)
            {
                var name = other.GetComponent<Player>()?.PlayerName;
                if (name == null || !partyNames.Contains(name))
                    continue;
                var isDead = other.IsDead;
                if (isDead && _deadPartyMembers.Add(name))
                    _activeRun.PartyDeaths++;
                if (!isDead)
                    _deadPartyMembers.Remove(name);
                _partyInArea.Add(new PartyObservation(name, other.GridPosNum, isDead));
            }
        }
        if (dead && members.All(member => !string.IsNullOrWhiteSpace(member.PlayerInfo?.CharacterName)) &&
            partyNames.All(name => _partyInArea.Any(other => other.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && other.Dead)))
            _activeRun.WipeObserved = true;
    }

    private LootKey DescribeRunItem(Entity item)
    {
        var type = GameController.Files.BaseItemTypes.Translate(item.Path);
        var baseName = type?.BaseName ?? item.Path.Split('/').Last();
        if (baseName == "Gold")
            return null;
        var mods = item.GetComponent<Mods>();
        var rarity = mods?.ItemRarity;
        if (rarity == ItemRarity.Unique)
            return new LootKey(item.Path, type == null ? baseName : GetUniqueDisplayName(item, type, mods), "Unique",
                UniqueType: UniqueItemCatalog.ItemType(item.Path, type?.ClassName));
        if (baseName == NinjaPriceCache.NightmareMap)
            return new LootKey(item.Path, baseName, "Map", item.GetComponent<MapKey>()?.Tier);
        var category = type == null ? "Item" : IsFragment(type, item.Path) ? "Fragment" :
            IsCurrency(type, item.Path) ? "Currency" : type.ClassName ?? "Item";
        if (rarity is ItemRarity.Magic or ItemRarity.Rare)
            category = rarity.ToString();
        return new LootKey(item.Path, baseName, category);
    }

    private void RecordGroundDrop(Entity world, Entity item)
    {
        if (_activeRun == null || world.Id == 0)
            return;

        var key = DescribeRunItem(item);
        if (key == null)
            return;
        var count = Math.Max(item.GetComponent<Stack>()?.Size ?? 1, 1);
        if (_groundDrops.TryGetValue(world.Id, out var previous))
        {
            if (previous.Key != key)
            {
                var original = _runLoot[previous.Key];
                GetRunLoot(key).Dropped += Math.Max(count, previous.Quantity);
                original.Dropped -= previous.Quantity;
            }
            else
            {
                GetRunLoot(key).Dropped += Math.Max(count - previous.Quantity, 0);
            }
            count = Math.Max(count, previous.Quantity);
        }
        else
        {
            GetRunLoot(key).Dropped += count;
        }
        _groundDrops[world.Id] = new GroundDrop(key, count, world.GridPosNum, world.Address);
    }

    private MapLoot GetRunLoot(LootKey key)
    {
        if (!_runLoot.TryGetValue(key, out var item))
        {
            item = new MapLoot(Guid.NewGuid(), key.Name, key.Category)
                { MapTier = key.MapTier, UniqueType = key.UniqueType, IsUniqueBelt = key.UniqueType == "Belts" };
            _runLoot.Add(key, item);
        }
        return item;
    }

    public override void EntityRemoved(Entity entity)
    {
        try
        {
            if (_activeRun == null || !_trackingCurrentArea || entity == null ||
                !_groundDrops.TryGetValue(entity.Id, out var drop) || _resolvedGroundDrops.Contains(entity.Id) ||
                GameController.IsLoading ||
                GameController.Game.AreaChangeCount != _lastAreaChangeCount ||
                !IsSameMapInstance(GameController.Area.CurrentArea, allowUnknown: true) ||
                (entity.Address != 0 && drop.Address != entity.Address))
                return;

            var local = GameController.Player;
            if (local?.IsValid != true)
                return;
            var localDistance = Vector2.DistanceSquared(local.GridPosNum, drop.Position);
            var nearbyPlayer = localDistance <= PartyObservationRangeSquared
                ? _partyInArea.Where(p => !p.Dead && Vector2.DistanceSquared(p.Position, drop.Position) <= PickupRangeSquared)
                    .OrderBy(p => Vector2.DistanceSquared(p.Position, drop.Position)).FirstOrDefault()
                : null;
            if (localDistance > PickupRangeSquared && nearbyPlayer == null)
                return;
            if (_pickupCandidates.Any(x => x.Id == entity.Id))
                return;
            _pickupCandidates.Add(new PickupCandidate(entity.Id, drop, Environment.TickCount64, nearbyPlayer?.Name));
        }
        catch (Exception ex)
        {
            LogError($"Could not observe a ground item removal: {ex.Message}");
        }
    }

    private Dictionary<LootKey, int> ReadInventory()
    {
        var inventory = GameController.IngameState.ServerData?.GetPlayerInventoryBySlot(InventorySlotE.MainInventory1);
        if (inventory is not { Address: > 0, Columns: > 0, Items: { } items })
            return null;

        var counts = new Dictionary<LootKey, int>();
        foreach (var item in items)
        {
            if (item?.IsValid != true || string.IsNullOrWhiteSpace(item.Path))
                return null;
            var key = DescribeRunItem(item);
            if (key != null)
                counts[key] = counts.GetValueOrDefault(key) + Math.Max(item.GetComponent<Stack>()?.Size ?? 1, 1);
        }
        return counts;
    }

    private void MatchPickups(long now)
    {
        var counts = ReadInventory();
        if (counts != null)
        {
            foreach (var (key, count) in counts)
            {
                if (_inventoryCounts == null || count <= _inventoryCounts.GetValueOrDefault(key))
                    continue;
                _inventoryGains.TryGetValue(key, out var old);
                var carry = old != null && now - old.Seen <= PickupEvidenceMs ? old.Count : 0;
                _inventoryGains[key] = new InventoryGain(carry + count - _inventoryCounts.GetValueOrDefault(key), now);
            }
            _inventoryCounts = counts;
        }

        for (var i = 0; i < _pickupCandidates.Count;)
        {
            var pending = _pickupCandidates[i];
            var age = now - pending.Seen;
            var drop = pending.Drop;
            _inventoryGains.TryGetValue(drop.Key, out var gain);
            var local = gain != null && Math.Abs(gain.Seen - pending.Seen) <= PickupEvidenceMs
                ? Math.Min(drop.Quantity, gain.Count)
                : 0;
            // Give the inventory time to update before attributing the remaining units to the party.
            if (age < PickupPollMs || (local < drop.Quantity && age < PickupEvidenceMs))
            {
                i++;
                continue;
            }

            _pickupCandidates.RemoveAt(i);
            if (EntityListWrapper.GetEntityById(pending.Id) is { IsValid: true } ||
                GameController.SleepingEntityListWrapper?.NotValidDict?.ContainsKey(pending.Id) == true)
                continue;

            var party = Settings.Dashboard.EstimatePartyPickups && pending.NearbyPartyMember != null
                ? drop.Quantity - local
                : 0;
            if (local == 0 && party == 0)
                continue;

            _resolvedGroundDrops.Add(pending.Id);
            var loot = GetRunLoot(drop.Key);
            loot.Collected += local;
            loot.Estimated += party;
            if (party > 0)
            {
                var collectors = loot.Collectors ??= new(StringComparer.OrdinalIgnoreCase);
                collectors[pending.NearbyPartyMember] = collectors.GetValueOrDefault(pending.NearbyPartyMember) + party;
            }
            if (local > 0)
                _inventoryGains[drop.Key] = gain with { Count = gain.Count - local };
            _nextRunSave = Math.Min(_nextRunSave, now + 350);
        }
        foreach (var key in _inventoryGains.Where(pair => now - pair.Value.Seen > PickupEvidenceMs).Select(pair => pair.Key).ToArray())
            _inventoryGains.Remove(key);
    }

    private void CaptureMarketSnapshot()
    {
        var tick = Environment.TickCount64;
        if (_priceArchive == null || tick < _nextMarketCapture || _scheduledMarketRead is { IsCompleted: false })
            return;

        _nextMarketCapture = tick + 60000;
        var league = GameController.IngameState?.ServerData?.League;
        var nowUtc = DateTime.UtcNow;
        if (!NinjaPriceCache.IsValidLeague(league) ||
            (_scheduledMarketRead is { IsCompletedSuccessfully: true, Result: { } previous } &&
             string.Equals(_scheduledMarketLeague, league, StringComparison.OrdinalIgnoreCase) &&
             MarketPriceArchive.Covers(previous, nowUtc)))
            return;

        _scheduledMarketLeague = league;
        _scheduledMarketRead = ReadMarketSnapshot(league, nowUtc);
    }

    private void RefreshMarket(long now)
    {
        AcceptMarket();
        var league = GameController.IngameState.ServerData?.League;
        if (string.IsNullOrWhiteSpace(_activeRun.League) && !string.IsNullOrWhiteSpace(league))
            _activeRun.League = league;
        if (_market != null || _marketRead != null || now < _nextPriceRead || string.IsNullOrWhiteSpace(_activeRun.League))
            return;

        _nextPriceRead = now + 60000;
        _marketRead = ReadMarketSnapshot(_activeRun.League, _activeRun.StartedUtc);
    }

    private Task<MarketPriceSnapshot> ReadMarketSnapshot(string league, DateTime startedUtc)
    {
        var archive = _priceArchive;
        var history = _history;
        return Task.Run(() =>
        {
            MarketPriceSnapshot snapshot = null;
            try
            {
                snapshot = archive.Get(league, startedUtc, DateTime.UtcNow);
                if (snapshot != null)
                    history.FillMarketPrices(league, snapshot);
            }
            catch (Exception ex)
            {
                LogError($"Could not load or apply market price snapshot: {ex.Message}");
            }

            return snapshot;
        });
    }

    private void AcceptMarket()
    {
        if (_marketRead?.IsCompleted != true)
            return;
        if (_marketRead.IsCompletedSuccessfully)
            _market = _marketRead.Result;
        else if (_marketRead.Exception is { } error)
            LogError($"Could not load market price snapshot: {error.GetBaseException().Message}");
        _marketRead = null;
    }

    private async Task FinishMarketRead(MapHistory history, Guid runId, Task<MarketPriceSnapshot> read)
    {
        try
        {
            if (await read.ConfigureAwait(false) is { } snapshot)
                history.FillMarketPrices(runId, snapshot);
        }
        catch (Exception ex)
        {
            LogError($"Could not fill saved loot prices: {ex.Message}");
        }
    }

    private const string PricerDirectoryName = "Get-Chaos-Value";

    private PluginWrapper FindPricer() => PluginManager?.Plugins.FirstOrDefault(plugin =>
        string.Equals(plugin.Plugin?.DirectoryName, PricerDirectoryName, StringComparison.OrdinalIgnoreCase) ||
        plugin.Plugin?.GetType().FullName == "Ninja_Price.Main.Main");

    private string GetPricerCacheRoot() => GetPricerCacheRoot(FindPricer());

    private string GetPricerCacheRoot(PluginWrapper pricer) => Path.Combine(pricer?.Plugin.DirectoryFullName ??
        Path.Combine(Path.GetDirectoryName(DirectoryFullName) ?? string.Empty, PricerDirectoryName), "NinjaData");

    private void UpdatePricingStatus()
    {
        var now = Environment.TickCount64;
        if (_priceArchive == null || now < _nextPricingStatusRead)
            return;
        _nextPricingStatusRead = now + 5000;

        var pricer = FindPricer();
        var league = GameController?.InGame == true && !GameController.IsLoading
            ? GameController.IngameState.ServerData?.League : null;
        var message = pricer switch
        {
            null => "Ninja Pricer unavailable. Install the separate Get-Chaos-Value plugin and enable it in Loader.",
            { IsEnable: false } => "Ninja Pricer is disabled. Enable it in Loader to keep prices updated.",
            _ when !NinjaPriceCache.IsValidLeague(league) => "Ninja Pricer is enabled. Select a character to check its league cache.",
            _ when File.Exists(Path.Combine(GetPricerCacheRoot(pricer), league, "Currency2.json")) =>
                $"Ninja Pricer cache found for {league}.",
            _ => $"Waiting for Ninja Pricer prices for {league}. Check the league and press Reload Prices in Ninja Pricer."
        };
        _pricingStatus = new PricingSourceStatus(message, _priceArchive.DirectoryPath, pricer == null);
    }

    private void SaveRunCheckpoint(string state = null)
    {
        if (_history == null || _activeRun == null)
            return;
        try
        {
            if (_trackingCurrentArea && _wasInGameLastTick)
                UpdateLiveTimers();
            _activeRun.State = state ?? _activeRun.State;
            _activeRun.UpdatedUtc = DateTime.UtcNow;
            _activeRun.Seconds = _currentAreaStats.Elapsed.TotalSeconds;
            _activeRun.Rarity = _currentAreaStats.CurrentItemRarity;
            _activeRun.Quantity = _currentAreaStats.CurrentItemQuantity;
            _activeRun.PackSize = _currentAreaStats.CurrentPackSize;
            _activeRun.MoreCurrency = _currentAreaStats.CurrentMoreCurrency;
            _activeRun.MoreMaps = _currentAreaStats.CurrentMoreMaps;
            _activeRun.MoreScarabs = _currentAreaStats.CurrentMoreScarabs;
            AcceptMarket();
            _activeRun.DropTotals = new MapDropTotals(_currentAreaStats.CurrencyQuantity, _currentAreaStats.UniqueItems,
                _currentAreaStats.NormalBaseItems, _currentAreaStats.MagicBaseItems, _currentAreaStats.RareBaseItems);
            _activeRun.T0UniquesDropped = _currentAreaStats.T0UniqueItems;
            _activeRun.LastT0Unique = string.IsNullOrWhiteSpace(_currentAreaStats.LastT0UniqueName)
                ? null : _currentAreaStats.LastT0UniqueName;
            _activeRun.ApplyMarketPrices(_market, _runLoot.Values);
            _activeRun.Loot = _runLoot.Values.Where(x => x.Dropped > 0 || x.Collected + x.Estimated > 0).ToList();
            _history.SaveLive(_activeRun);
            if (state is "recorded" or "interrupted" && _marketRead is { } read)
            {
                _ = FinishMarketRead(_history, _activeRun.Id, read);
                _marketRead = null;
            }
        }
        catch (Exception ex)
        {
            LogError($"Could not save map history: {ex}");
        }
    }
}
