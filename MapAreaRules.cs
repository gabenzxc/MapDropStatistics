using System;

namespace MapDropStatistics;

internal static class MapAreaRules
{
    public static bool IsExcludedFromMaps(string name, string areaId = null)
    {
        var normalized = name?.Trim().Replace('’', '\'');
        return string.Equals(normalized, "Aspirants' Plaza", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "The Fetid Fleshpit", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "The Swarmforge", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(areaId, "BeyondRealmKtash", StringComparison.Ordinal);
    }
}
