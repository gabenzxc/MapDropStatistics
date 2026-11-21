using System;

namespace MapDropStatistics;

internal enum MonsterKillKind { Regular, Auxiliary, EncounterUnique }

internal static class MonsterKillRules
{
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
