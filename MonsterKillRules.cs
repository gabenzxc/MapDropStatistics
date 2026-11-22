using System;

namespace MapDropStatistics;

internal enum MonsterKillKind { Regular, Auxiliary, EncounterUnique }

internal static class MonsterKillRules
{
    public static bool IsConfirmedDeath(bool seenAlive, bool hasMonster, bool isDead, int? currentHp, int? maxHp) =>
        seenAlive && hasMonster && isDead && currentHp == 0 && maxHp is > 0;

    public static MonsterKillKind Classify(string metadata, bool unique)
    {
        if (string.IsNullOrEmpty(metadata))
            return MonsterKillKind.Regular;

        var legion = metadata.StartsWith("Metadata/Monsters/LegionLeague/", StringComparison.Ordinal);
        var name = metadata.AsSpan(metadata.LastIndexOf('/') + 1);
        if (metadata.StartsWith("Metadata/Monsters/LeagueArchnemesis/LivingCrystal", StringComparison.Ordinal) ||
            metadata.StartsWith("Metadata/Monsters/VolatileCore/", StringComparison.Ordinal) ||
            (legion && name is "LegionKaruiGeneralClone" or "LegionKaruiGeneralFish") ||
            metadata == "Metadata/Monsters/AtlasInvaders/CleansingMonsters/CleansingPhantasmPossessionDemon")
            return MonsterKillKind.Auxiliary;

        // An observed death alone cannot establish the end of a scripted encounter or who landed the kill.
        if (unique && ((legion && name.EndsWith("General", StringComparison.Ordinal)) ||
            metadata == "Metadata/Monsters/AtlasInvaders/CleansingMonsters/CleansingPhantasm"))
            return MonsterKillKind.EncounterUnique;

        return MonsterKillKind.Regular;
    }
}
