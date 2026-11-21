using System;

namespace MapDropStatistics;

internal static class MapAreaRules
{
    public static bool IsExcludedFromMaps(string name)
    {
        var normalized = name?.Trim().Replace('’', '\'');
        return string.Equals(normalized, "Aspirants' Plaza", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "The Fetid Fleshpit", StringComparison.OrdinalIgnoreCase);
    }
}
