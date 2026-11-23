using System;

namespace MapDropStatistics;

internal readonly record struct MapInstanceIdentity(string AreaId, uint Hash, int InstanceId)
{
    public bool SameArea(MapInstanceIdentity other) => AreaId != null && AreaId == other.AreaId;

    public bool Matches(MapInstanceIdentity other) => SameArea(other) &&
        ((Hash != 0 && Hash == other.Hash) || (InstanceId != 0 && InstanceId == other.InstanceId));

    public bool CompatibleWith(MapInstanceIdentity other) => SameArea(other) && (Matches(other) ||
        ((Hash == 0 || other.Hash == 0) && (InstanceId == 0 || other.InstanceId == 0)));

    public static bool HasPriorMapTime(TimeSpan previous, TimeSpan beforeZoneIn) =>
        previous >= TimeSpan.FromSeconds(1) && beforeZoneIn >= TimeSpan.FromSeconds(1) &&
        beforeZoneIn >= previous - TimeSpan.FromSeconds(2);

    public static bool IsMirage(string areaDisplayName, string loadingAreaName) =>
        IsMirageName(areaDisplayName) || IsMirageName(loadingAreaName);

    private static bool IsMirageName(string name) =>
        name?.StartsWith("Mirage of ", StringComparison.OrdinalIgnoreCase) == true;
}
