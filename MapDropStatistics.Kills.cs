using System.Collections.Generic;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;

namespace MapDropStatistics;

public partial class MapDropStatistics
{
    private const int MonsterScanIntervalMs = 75;
    private readonly HashSet<uint> _livingMonsters = [];
    private readonly HashSet<uint> _countedMonsterKills = [];
    private long _nextMonsterScan;

    private void ResetMonsterCounting()
    {
        _livingMonsters.Clear();
        _countedMonsterKills.Clear();
        _nextMonsterScan = 0;
    }

    private void ObserveMonsterKills(long now)
    {
        if (now < _nextMonsterScan)
            return;
        _nextMonsterScan = now + MonsterScanIntervalMs;
        if (GameController.Player?.IsValid != true)
            return;

        _activeRun.Kills ??= new MapKillCounts();
        if (GameController.EntityListWrapper.ValidEntitiesByType.TryGetValue(EntityType.Monster, out var monsters))
        {
            foreach (var entity in monsters)
                ObserveMonsterKill(entity);
        }
    }

    private void ObserveMonsterKill(Entity entity)
    {
        if (_activeRun == null || entity is not { IsValid: true, Id: > 0 } || _countedMonsterKills.Contains(entity.Id))
            return;

        var seenAlive = _livingMonsters.Contains(entity.Id);
        if (entity.IsAlive)
        {
            if (!seenAlive && entity.IsTargetable && entity.IsHostile && entity.HasComponent<Monster>() &&
                entity.HasComponent<ObjectMagicProperties>() && !entity.IsDead &&
                entity.GetComponent<Life>() is { CurHP: > 0, MaxHP: > 0 })
                _livingMonsters.Add(entity.Id);
            return;
        }

        if (!seenAlive || !entity.IsHostile || !entity.TryGetComponent<ObjectMagicProperties>(out var properties))
            return;

        var rarity = properties.Rarity;
        if (rarity is not (MonsterRarity.White or MonsterRarity.Magic or MonsterRarity.Rare or MonsterRarity.Unique))
            return;

        var life = entity.GetComponent<Life>();
        if (!MonsterKillRules.IsConfirmedDeath(seenAlive, entity.HasComponent<Monster>(), entity.IsDead, life?.CurHP, life?.MaxHP))
            return;

        _countedMonsterKills.Add(entity.Id);
        _livingMonsters.Remove(entity.Id);
        var counts = _activeRun.Kills ?? new MapKillCounts();
        var kind = MonsterKillRules.Classify(entity.Metadata, rarity == MonsterRarity.Unique);
        _activeRun.Kills = kind switch
        {
            MonsterKillKind.Auxiliary => counts,
            MonsterKillKind.EncounterUnique => counts with { EncounterUnique = counts.EncounterUnique + 1 },
            _ => rarity switch
            {
                MonsterRarity.White => counts with { Normal = counts.Normal + 1 },
                MonsterRarity.Magic => counts with { Magic = counts.Magic + 1 },
                MonsterRarity.Rare => counts with { Rare = counts.Rare + 1 },
                _ => counts with { Unique = counts.Unique + 1 }
            }
        };
    }
}
