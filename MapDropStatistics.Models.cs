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

    private sealed class PendingAreaReview
    {
        public string AreaName { get; set; } = string.Empty;
        public bool IsFailMap { get; set; }
        public AreaLootStats Stats { get; set; } = new();
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
