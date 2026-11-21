using System.Linq;

namespace MapDropStatistics;

internal static class MapFailureRules
{
    public const int DefaultUniqueThreshold = 200;

    public static bool IsFailed(string state, bool? manual, bool wipeObserved, int uniquesDropped, int threshold) =>
        manual ?? (wipeObserved ||
            (state is "recorded" or "interrupted" or "legacy" && uniquesDropped <= threshold));
}

internal sealed partial class MapHistory
{
    private int _failUniqueThreshold;

    public void SetFailUniqueThreshold(int threshold)
    {
        lock (_gate)
        {
            if (_failUniqueThreshold == threshold)
                return;

            _failUniqueThreshold = threshold;
            foreach (var (id, map) in _index.ToArray())
            {
                var failed = MapFailureRules.IsFailed(map.State, map.FailureOverride, map.WipeObserved,
                    map.UniquesDropped, threshold);
                if (map.Failed == failed)
                    continue;

                _index[id] = map with { Failed = failed };
                _indexWithUniques[id] = _indexWithUniques[id] with { Failed = failed };
            }

            _widgetSessionStats = null;
            if (_widgetContext != null)
                PublishWidget(_widgetContext);
        }
    }
}
