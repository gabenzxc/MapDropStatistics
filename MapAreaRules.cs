using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MapDropStatistics;

internal static class MapAreaRules
{
    private static readonly HashSet<string> AtlasAreaIds;
    private static readonly HashSet<string> AtlasAreaNames;

    static MapAreaRules()
    {
        using var stream = typeof(MapAreaRules).Assembly.GetManifestResourceStream("AtlasMapAreas.json")
            ?? throw new InvalidDataException("Atlas map catalog is missing.");
        var catalog = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidDataException("Atlas map catalog is empty.");
        AtlasAreaIds = catalog.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        AtlasAreaNames = catalog.Values.Select(NormalizeName).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsAtlasMap(string areaId, string name = null) =>
        string.IsNullOrWhiteSpace(areaId)
            ? AtlasAreaNames.Contains(NormalizeName(name))
            : AtlasAreaIds.Contains(areaId);

    public static bool IsAtlasMap(MapRun run) => IsAtlasMap(GetRecordedAreaId(run), run.Name);

    public static string AreaIdFromKey(string areaKey)
    {
        var separator = areaKey?.IndexOf('|') ?? -1;
        return separator > 0 ? areaKey[..separator] : null;
    }

    private static string GetRecordedAreaId(MapRun run)
    {
        if (!string.IsNullOrWhiteSpace(run.AreaId))
            return run.AreaId;

        var start = run.EncounterSignals?.FirstOrDefault(signal => signal.Source == "Area");
        if (start == null)
            return null;

        // Older records used the area name as the detail when the client supplied no ID.
        var detail = NormalizeName(start.Detail);
        return string.Equals(detail, NormalizeName(start.Area ?? run.Name), StringComparison.OrdinalIgnoreCase)
            ? null
            : start.Detail;
    }

    private static string NormalizeName(string name) => name?.Trim().Replace('\u2019', '\'');
}
