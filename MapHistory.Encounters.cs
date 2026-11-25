using System;
using System.Text.Json.Serialization;

namespace MapDropStatistics;

internal sealed record MapEncounterProgress(int LegionSeen = 0, int LegionClosed = 0, int AbyssSeen = 0,
    int AbyssClosed = 0, bool MirageActivated = false, bool DeliriumStarted = false)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool WildwoodEntered { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int BreachSeen { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int BreachClosed { get; init; }
}

internal enum ProgressEncounter { None, Legion, EndlessLegion, Abyss, Mirage, Delirium, UnstableBreach, BreachHive }

internal static class EncounterProgressRules
{
    internal const string DeliriumMirror = "Metadata/MiscellaneousObjects/Affliction/AfflictionInitiator";
    internal const string LegionShatteredState = "shattered";
    internal const string WildwoodAreaId = "AzmeriLeagueArea";
    internal const string WildwoodInsideSignal = "IsInsideAzmeriZone = true";

    public static ProgressEncounter Identify(string metadata) => metadata switch
    {
        "Metadata/Terrain/Leagues/Legion/Objects/LegionInitiator" or
        "Metadata/Terrain/Leagues/Legion/Objects/ForcedLegionInitiator" => ProgressEncounter.Legion,
        "Metadata/Terrain/Leagues/Legion/Objects/LegionEndlessInitiator" => ProgressEncounter.EndlessLegion,
        "Metadata/MiscellaneousObjects/Abyss/AbyssNodeBase" or
        "Metadata/MiscellaneousObjects/Abyss/AbyssNodeLarge" or
        "Metadata/MiscellaneousObjects/Abyss/AbyssNodeSmall" or
        "Metadata/MiscellaneousObjects/Abyss/AbyssNodeSmallMortarSpawned" or
        "Metadata/MiscellaneousObjects/Abyss/AbyssNodeSmallMortarSpawned2" or
        "Metadata/MiscellaneousObjects/Abyss/AbyssFinalNodeBase" or
        "Metadata/MiscellaneousObjects/Abyss/AbyssFinalNodeSubArea" => ProgressEncounter.Abyss,
        "Metadata/MiscellaneousObjects/Faridun/DjinnPortal" => ProgressEncounter.Mirage,
        DeliriumMirror => ProgressEncounter.Delirium,
        _ when string.Equals(metadata, "Metadata/MiscellaneousObjects/Brequel/BrequelInitiator", StringComparison.OrdinalIgnoreCase)
            => ProgressEncounter.UnstableBreach,
        _ when string.Equals(metadata, "Metadata/MiscellaneousObjects/Brequel/BrequelStabiliser", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(metadata, "Metadata/MiscellaneousObjects/Brequel/BrequelStabiliserFortress", StringComparison.OrdinalIgnoreCase)
            => ProgressEncounter.BreachHive,
        _ => ProgressEncounter.None
    };

    public static string SharedState(ProgressEncounter kind) => kind switch
    {
        ProgressEncounter.Legion or ProgressEncounter.EndlessLegion => "obelisk_state",
        ProgressEncounter.Mirage => "ready",
        ProgressEncounter.Delirium => "interacted",
        ProgressEncounter.UnstableBreach => "encounter_state",
        ProgressEncounter.BreachHive => "bomb_defence",
        _ => null
    };

    public static bool IsComplete(ProgressEncounter kind, long? state, long? legionShattered = null) => kind switch
    {
        // The normal and forced monoliths hide their minimap marker at shattered=2.
        ProgressEncounter.Legion => state == 4 || legionShattered == 2,
        ProgressEncounter.EndlessLegion => state == 5,
        // Abyss transition 3 closes a failed pit. Transition 4 closes a successful pit.
        ProgressEncounter.Abyss => state == 4,
        ProgressEncounter.Mirage or ProgressEncounter.Delirium => state == 1,
        // Both terminal states close the rift. They differ in whether it stabilized first.
        ProgressEncounter.UnstableBreach => state is 3 or 4,
        // Hive defense succeeds at state 2 and fails at state 3.
        ProgressEncounter.BreachHive => state == 2,
        _ => false
    };
}
