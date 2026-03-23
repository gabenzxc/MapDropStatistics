using System;
using System.Collections.Generic;
using ExileCore.PoEMemory.MemoryObjects;
using SharpDX;

namespace MapDropStatistics;

public partial class MapDropStatistics
{
    private sealed class AreaLootStats
    {
        public int UniqueItems { get; set; }
        public int T0UniqueItems { get; set; }
        public string LastT0UniqueName { get; set; } = string.Empty;
        public int CurrencyStacks { get; set; }
        public int CurrencyQuantity { get; set; }
        public int FragmentStacks { get; set; }
        public int FragmentQuantity { get; set; }
        public int NormalBaseItems { get; set; }
        public int MagicBaseItems { get; set; }
        public int RareBaseItems { get; set; }
        public int DivineOrbQuantity { get; set; }
        public int ValdosPuzzleBoxQuantity { get; set; }
        public TimeSpan Elapsed { get; set; }
        public Dictionary<string, int> DropCounts { get; } = new(StringComparer.InvariantCultureIgnoreCase);
        public Dictionary<string, int> CustomTrackedCounts { get; } = new(StringComparer.InvariantCultureIgnoreCase);
        public bool HasDrops => UniqueItems > 0 || T0UniqueItems > 0 || CurrencyQuantity > 0 || FragmentQuantity > 0 || NormalBaseItems > 0 || MagicBaseItems > 0 || RareBaseItems > 0;

        public void AddDrop(string name, int quantity)
        {
            DropCounts[name] = DropCounts.TryGetValue(name, out var existing) ? existing + quantity : quantity;
        }

        public void Reset()
        {
            UniqueItems = 0;
            T0UniqueItems = 0;
            LastT0UniqueName = string.Empty;
            CurrencyStacks = 0;
            CurrencyQuantity = 0;
            FragmentStacks = 0;
            FragmentQuantity = 0;
            NormalBaseItems = 0;
            MagicBaseItems = 0;
            RareBaseItems = 0;
            DivineOrbQuantity = 0;
            ValdosPuzzleBoxQuantity = 0;
            Elapsed = TimeSpan.Zero;
            DropCounts.Clear();
            CustomTrackedCounts.Clear();
        }

        public AreaLootStats Clone()
        {
            var clone = new AreaLootStats
            {
                UniqueItems = UniqueItems,
                T0UniqueItems = T0UniqueItems,
                LastT0UniqueName = LastT0UniqueName,
                CurrencyStacks = CurrencyStacks,
                CurrencyQuantity = CurrencyQuantity,
                FragmentStacks = FragmentStacks,
                FragmentQuantity = FragmentQuantity,
                NormalBaseItems = NormalBaseItems,
                MagicBaseItems = MagicBaseItems,
                RareBaseItems = RareBaseItems,
                DivineOrbQuantity = DivineOrbQuantity,
                ValdosPuzzleBoxQuantity = ValdosPuzzleBoxQuantity,
                Elapsed = Elapsed
            };

            foreach (var (key, value) in DropCounts)
                clone.DropCounts[key] = value;

            foreach (var (key, value) in CustomTrackedCounts)
                clone.CustomTrackedCounts[key] = value;

            return clone;
        }
    }

    private sealed class AreaReview
    {
        public string AreaName { get; set; } = string.Empty;
        public bool IsFailMap { get; set; }
        public AreaLootStats Stats { get; set; } = new();
    }

    private sealed class AreaReviewSnapshot
    {
        public string AreaName { get; set; } = string.Empty;
        public bool IsFailMap { get; set; }
        public AreaLootStatsSnapshot Stats { get; set; } = new();

        public static AreaReviewSnapshot FromReview(AreaReview review)
        {
            return new AreaReviewSnapshot
            {
                AreaName = review.AreaName,
                IsFailMap = review.IsFailMap,
                Stats = AreaLootStatsSnapshot.FromAreaLootStats(review.Stats)
            };
        }

        public AreaReview ToReview()
        {
            return new AreaReview
            {
                AreaName = AreaName ?? string.Empty,
                IsFailMap = IsFailMap,
                Stats = Stats?.ToAreaLootStats() ?? new AreaLootStats()
            };
        }
    }

    private sealed class AreaLootStatsSnapshot
    {
        public int UniqueItems { get; set; }
        public int T0UniqueItems { get; set; }
        public string LastT0UniqueName { get; set; } = string.Empty;
        public int CurrencyStacks { get; set; }
        public int CurrencyQuantity { get; set; }
        public int FragmentStacks { get; set; }
        public int FragmentQuantity { get; set; }
        public int NormalBaseItems { get; set; }
        public int MagicBaseItems { get; set; }
        public int RareBaseItems { get; set; }
        public int DivineOrbQuantity { get; set; }
        public int ValdosPuzzleBoxQuantity { get; set; }
        public long ElapsedTicks { get; set; }
        public Dictionary<string, int> DropCounts { get; set; } = new(StringComparer.InvariantCultureIgnoreCase);
        public Dictionary<string, int> CustomTrackedCounts { get; set; } = new(StringComparer.InvariantCultureIgnoreCase);

        public static AreaLootStatsSnapshot FromAreaLootStats(AreaLootStats stats)
        {
            return new AreaLootStatsSnapshot
            {
                UniqueItems = stats.UniqueItems,
                T0UniqueItems = stats.T0UniqueItems,
                LastT0UniqueName = stats.LastT0UniqueName,
                CurrencyStacks = stats.CurrencyStacks,
                CurrencyQuantity = stats.CurrencyQuantity,
                FragmentStacks = stats.FragmentStacks,
                FragmentQuantity = stats.FragmentQuantity,
                NormalBaseItems = stats.NormalBaseItems,
                MagicBaseItems = stats.MagicBaseItems,
                RareBaseItems = stats.RareBaseItems,
                DivineOrbQuantity = stats.DivineOrbQuantity,
                ValdosPuzzleBoxQuantity = stats.ValdosPuzzleBoxQuantity,
                ElapsedTicks = stats.Elapsed.Ticks,
                DropCounts = new Dictionary<string, int>(stats.DropCounts, StringComparer.InvariantCultureIgnoreCase),
                CustomTrackedCounts = new Dictionary<string, int>(stats.CustomTrackedCounts, StringComparer.InvariantCultureIgnoreCase)
            };
        }

        public AreaLootStats ToAreaLootStats()
        {
            var stats = new AreaLootStats
            {
                UniqueItems = Math.Max(UniqueItems, 0),
                T0UniqueItems = Math.Max(T0UniqueItems, 0),
                LastT0UniqueName = LastT0UniqueName ?? string.Empty,
                CurrencyStacks = Math.Max(CurrencyStacks, 0),
                CurrencyQuantity = Math.Max(CurrencyQuantity, 0),
                FragmentStacks = Math.Max(FragmentStacks, 0),
                FragmentQuantity = Math.Max(FragmentQuantity, 0),
                NormalBaseItems = Math.Max(NormalBaseItems, 0),
                MagicBaseItems = Math.Max(MagicBaseItems, 0),
                RareBaseItems = Math.Max(RareBaseItems, 0),
                DivineOrbQuantity = Math.Max(DivineOrbQuantity, 0),
                ValdosPuzzleBoxQuantity = Math.Max(ValdosPuzzleBoxQuantity, 0),
                Elapsed = TimeSpan.FromTicks(Math.Max(ElapsedTicks, 0))
            };

            foreach (var (key, value) in DropCounts ?? new Dictionary<string, int>(StringComparer.InvariantCultureIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(key))
                    stats.DropCounts[key] = Math.Max(value, 0);
            }

            foreach (var (key, value) in CustomTrackedCounts ?? new Dictionary<string, int>(StringComparer.InvariantCultureIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(key))
                    stats.CustomTrackedCounts[key] = Math.Max(value, 0);
            }

            return stats;
        }
    }

    private sealed class SessionLootStats
    {
        public int AreasTracked { get; private set; }
        public int FailedAreas { get; private set; }
        public string LastT0UniqueName { get; set; } = string.Empty;
        public int TotalUniqueItems { get; private set; }
        public int TotalT0UniqueItems { get; private set; }
        public int TotalCurrencyQuantity { get; private set; }
        public int TotalFragmentQuantity { get; private set; }
        public int TotalNormalBaseItems { get; private set; }
        public int TotalMagicBaseItems { get; private set; }
        public int TotalRareBaseItems { get; private set; }
        public int TotalDivineOrbQuantity { get; private set; }
        public int TotalValdosPuzzleBoxQuantity { get; private set; }
        public TimeSpan TotalMapTime { get; private set; }
        public TimeSpan TotalNonMapTime { get; private set; }
        public TimeSpan CurrentNonMapElapsed { get; set; }
        public Dictionary<string, int> CustomTrackedTotals { get; } = new(StringComparer.InvariantCultureIgnoreCase);

        public double AverageUniqueItems => AreasTracked == 0 ? 0 : (double)TotalUniqueItems / AreasTracked;
        public double AverageT0UniqueItems => AreasTracked == 0 ? 0 : (double)TotalT0UniqueItems / AreasTracked;
        public double AverageCurrencyQuantity => AreasTracked == 0 ? 0 : (double)TotalCurrencyQuantity / AreasTracked;
        public double AverageFragmentQuantity => AreasTracked == 0 ? 0 : (double)TotalFragmentQuantity / AreasTracked;
        public double AverageNormalBaseItems => AreasTracked == 0 ? 0 : (double)TotalNormalBaseItems / AreasTracked;
        public double AverageMagicBaseItems => AreasTracked == 0 ? 0 : (double)TotalMagicBaseItems / AreasTracked;
        public double AverageRareBaseItems => AreasTracked == 0 ? 0 : (double)TotalRareBaseItems / AreasTracked;
        public double AverageDivineOrbQuantity => AreasTracked == 0 ? 0 : (double)TotalDivineOrbQuantity / AreasTracked;
        public double AverageValdosPuzzleBoxQuantity => AreasTracked == 0 ? 0 : (double)TotalValdosPuzzleBoxQuantity / AreasTracked;
        public TimeSpan AverageMapTime => AreasTracked == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(TotalMapTime.Ticks / AreasTracked);

        public void Add(AreaLootStats areaStats)
        {
            AreasTracked++;
            TotalUniqueItems += areaStats.UniqueItems;
            TotalT0UniqueItems += areaStats.T0UniqueItems;
            TotalCurrencyQuantity += areaStats.CurrencyQuantity;
            TotalFragmentQuantity += areaStats.FragmentQuantity;
            TotalNormalBaseItems += areaStats.NormalBaseItems;
            TotalMagicBaseItems += areaStats.MagicBaseItems;
            TotalRareBaseItems += areaStats.RareBaseItems;
            TotalDivineOrbQuantity += areaStats.DivineOrbQuantity;
            TotalValdosPuzzleBoxQuantity += areaStats.ValdosPuzzleBoxQuantity;
            TotalMapTime += areaStats.Elapsed;

            foreach (var (itemName, quantity) in areaStats.CustomTrackedCounts)
            {
                CustomTrackedTotals[itemName] = CustomTrackedTotals.TryGetValue(itemName, out var existing)
                    ? existing + quantity
                    : quantity;
            }
        }

        public void AddFail()
        {
            FailedAreas++;
        }

        public void Remove(AreaLootStats areaStats)
        {
            AreasTracked = Math.Max(AreasTracked - 1, 0);
            TotalUniqueItems = Math.Max(TotalUniqueItems - areaStats.UniqueItems, 0);
            TotalT0UniqueItems = Math.Max(TotalT0UniqueItems - areaStats.T0UniqueItems, 0);
            TotalCurrencyQuantity = Math.Max(TotalCurrencyQuantity - areaStats.CurrencyQuantity, 0);
            TotalFragmentQuantity = Math.Max(TotalFragmentQuantity - areaStats.FragmentQuantity, 0);
            TotalNormalBaseItems = Math.Max(TotalNormalBaseItems - areaStats.NormalBaseItems, 0);
            TotalMagicBaseItems = Math.Max(TotalMagicBaseItems - areaStats.MagicBaseItems, 0);
            TotalRareBaseItems = Math.Max(TotalRareBaseItems - areaStats.RareBaseItems, 0);
            TotalDivineOrbQuantity = Math.Max(TotalDivineOrbQuantity - areaStats.DivineOrbQuantity, 0);
            TotalValdosPuzzleBoxQuantity = Math.Max(TotalValdosPuzzleBoxQuantity - areaStats.ValdosPuzzleBoxQuantity, 0);
            TotalMapTime = TotalMapTime > areaStats.Elapsed ? TotalMapTime - areaStats.Elapsed : TimeSpan.Zero;

            foreach (var (itemName, quantity) in areaStats.CustomTrackedCounts)
            {
                if (!CustomTrackedTotals.TryGetValue(itemName, out var existing))
                    continue;

                var updated = existing - quantity;
                if (updated > 0)
                    CustomTrackedTotals[itemName] = updated;
                else
                    CustomTrackedTotals.Remove(itemName);
            }
        }

        public void RemoveFail()
        {
            FailedAreas = Math.Max(FailedAreas - 1, 0);
        }

        public void AddNonMapTime(TimeSpan elapsed)
        {
            if (elapsed > TimeSpan.Zero)
                TotalNonMapTime += elapsed;
        }

        public void Reset()
        {
            AreasTracked = 0;
            FailedAreas = 0;
            TotalUniqueItems = 0;
            TotalT0UniqueItems = 0;
            LastT0UniqueName = string.Empty;
            TotalCurrencyQuantity = 0;
            TotalFragmentQuantity = 0;
            TotalNormalBaseItems = 0;
            TotalMagicBaseItems = 0;
            TotalRareBaseItems = 0;
            TotalDivineOrbQuantity = 0;
            TotalValdosPuzzleBoxQuantity = 0;
            TotalMapTime = TimeSpan.Zero;
            TotalNonMapTime = TimeSpan.Zero;
            CurrentNonMapElapsed = TimeSpan.Zero;
            CustomTrackedTotals.Clear();
        }

        public void Load(SessionSnapshot snapshot)
        {
            AreasTracked = Math.Max(snapshot.AreasTracked, 0);
            FailedAreas = Math.Max(snapshot.FailedAreas, 0);
            LastT0UniqueName = snapshot.LastT0UniqueName ?? string.Empty;
            TotalUniqueItems = Math.Max(snapshot.TotalUniqueItems, 0);
            TotalT0UniqueItems = Math.Max(snapshot.TotalT0UniqueItems, 0);
            TotalCurrencyQuantity = Math.Max(snapshot.TotalCurrencyQuantity, 0);
            TotalFragmentQuantity = Math.Max(snapshot.TotalFragmentQuantity, 0);
            TotalNormalBaseItems = Math.Max(snapshot.TotalNormalBaseItems, 0);
            TotalMagicBaseItems = Math.Max(snapshot.TotalMagicBaseItems, 0);
            TotalRareBaseItems = Math.Max(snapshot.TotalRareBaseItems, 0);
            TotalDivineOrbQuantity = Math.Max(snapshot.TotalDivineOrbQuantity, 0);
            TotalValdosPuzzleBoxQuantity = Math.Max(snapshot.TotalValdosPuzzleBoxQuantity, 0);
            TotalMapTime = TimeSpan.FromTicks(Math.Max(snapshot.TotalMapTimeTicks, 0));
            TotalNonMapTime = TimeSpan.FromTicks(Math.Max(snapshot.TotalNonMapTimeTicks, 0));
            CurrentNonMapElapsed = TimeSpan.Zero;
            CustomTrackedTotals.Clear();

            foreach (var (itemName, quantity) in snapshot.CustomTrackedTotals ?? new Dictionary<string, int>(StringComparer.InvariantCultureIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(itemName))
                    continue;

                CustomTrackedTotals[itemName] = Math.Max(quantity, 0);
            }
        }

        public double GetAverageCustomTrackedDrop(string itemName)
        {
            if (AreasTracked == 0 || string.IsNullOrWhiteSpace(itemName))
                return 0;

            return CustomTrackedTotals.TryGetValue(itemName, out var quantity)
                ? (double)quantity / AreasTracked
                : 0;
        }
    }

    private sealed class TrackedDropWindowStats
    {
        public Dictionary<string, int> TotalCounts { get; } = new(StringComparer.InvariantCultureIgnoreCase);
        public Dictionary<string, int> SessionCounts { get; } = new(StringComparer.InvariantCultureIgnoreCase);

        public void Add(string itemName, int quantity)
        {
            if (string.IsNullOrWhiteSpace(itemName) || quantity <= 0)
                return;

            TotalCounts[itemName] = TotalCounts.TryGetValue(itemName, out var totalExisting)
                ? totalExisting + quantity
                : quantity;

            SessionCounts[itemName] = SessionCounts.TryGetValue(itemName, out var sessionExisting)
                ? sessionExisting + quantity
                : quantity;
        }

        public void ResetAll()
        {
            TotalCounts.Clear();
            SessionCounts.Clear();
        }

        public void ResetSession()
        {
            SessionCounts.Clear();
        }

        public void Load(TrackedDropWindowSnapshot snapshot)
        {
            TotalCounts.Clear();
            SessionCounts.Clear();

            foreach (var (itemName, quantity) in snapshot.TotalCounts ?? new Dictionary<string, int>(StringComparer.InvariantCultureIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(itemName) && quantity > 0)
                    TotalCounts[itemName] = quantity;
            }

            foreach (var (itemName, quantity) in snapshot.SessionCounts ?? new Dictionary<string, int>(StringComparer.InvariantCultureIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(itemName) && quantity > 0)
                    SessionCounts[itemName] = quantity;
            }
        }
    }

    private sealed class PendingDropInfo
    {
        public long DropKey { get; set; }
        public string PersistentItemKey { get; set; } = string.Empty;
        public Entity WorldEntity { get; set; }
        public Entity ItemEntity { get; set; }
        public string Reason { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string BaseName { get; set; } = string.Empty;
        public string ProvisionalStackCategory { get; set; } = string.Empty;
        public int ProvisionalStackQuantity { get; set; }
        public DateTime LastSeenUtc { get; set; }
    }

    private sealed class AreaDump
    {
        public string AreaName { get; set; } = string.Empty;
        public string AreaKey { get; set; } = string.Empty;
        public DateTime SavedAtUtc { get; set; }
        public int UniqueItems { get; set; }
        public int T0UniqueItems { get; set; }
        public int CurrencyStacks { get; set; }
        public int CurrencyQuantity { get; set; }
        public int FragmentStacks { get; set; }
        public int FragmentQuantity { get; set; }
        public int NormalBaseItems { get; set; }
        public int MagicBaseItems { get; set; }
        public int RareBaseItems { get; set; }
        public int DivineOrbQuantity { get; set; }
        public int ValdosPuzzleBoxQuantity { get; set; }
        public Dictionary<string, int> CustomTracked { get; set; } = new(StringComparer.InvariantCultureIgnoreCase);
        public Dictionary<string, int> Drops { get; set; } = new(StringComparer.InvariantCultureIgnoreCase);
        public Dictionary<string, PendingDumpInfo> Pending { get; set; } = new(StringComparer.InvariantCultureIgnoreCase);
    }

    private sealed class PendingDumpInfo
    {
        public long DropKey { get; set; }
        public string PersistentItemKey { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string BaseName { get; set; } = string.Empty;
        public string ProvisionalStackCategory { get; set; } = string.Empty;
        public int ProvisionalStackQuantity { get; set; }
        public DateTime LastSeenUtc { get; set; }
    }

    private sealed class SessionSnapshot
    {
        public DateTime SavedAtUtc { get; set; }
        public int AreasTracked { get; set; }
        public int FailedAreas { get; set; }
        public string LastT0UniqueName { get; set; } = string.Empty;
        public int TotalUniqueItems { get; set; }
        public int TotalT0UniqueItems { get; set; }
        public int TotalCurrencyQuantity { get; set; }
        public int TotalFragmentQuantity { get; set; }
        public int TotalNormalBaseItems { get; set; }
        public int TotalMagicBaseItems { get; set; }
        public int TotalRareBaseItems { get; set; }
        public int TotalDivineOrbQuantity { get; set; }
        public int TotalValdosPuzzleBoxQuantity { get; set; }
        public long TotalMapTimeTicks { get; set; }
        public long TotalNonMapTimeTicks { get; set; }
        public Dictionary<string, int> CustomTrackedTotals { get; set; } = new(StringComparer.InvariantCultureIgnoreCase);
        public TrackedDropWindowSnapshot TrackedDropWindow { get; set; } = new();
        public List<AreaReviewSnapshot> AppliedAreaReviews { get; set; } = [];
    }

    private sealed class TrackedDropWindowSnapshot
    {
        public Dictionary<string, int> TotalCounts { get; set; } = new(StringComparer.InvariantCultureIgnoreCase);
        public Dictionary<string, int> SessionCounts { get; set; } = new(StringComparer.InvariantCultureIgnoreCase);
    }

    private readonly record struct DisplaySegment(string Text, Color Color);
    private readonly record struct DisplayLine(bool AlignColumns, params DisplaySegment[] Segments)
    {
        public DisplayLine(params DisplaySegment[] segments) : this(false, segments)
        {
        }
    }

    private readonly record struct ColumnLayout(float LabelWidth, float Value1Width, float DividerWidth, float Value2Width)
    {
        public float TotalWidth => LabelWidth + Value1Width + DividerWidth + Value2Width;
    }

}
