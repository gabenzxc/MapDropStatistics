using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using ExileCore.Shared.Enums;
using ImGuiNET;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace MapDropStatistics;

public partial class MapDropStatistics
{
    private List<DisplayLine> BuildLines()
    {
        UpdateLiveTimers();
        UpdateCurrentAreaMapModifierStats();

        Color textColor = Settings.Display.Visuals.TextColor;
        Color uniqueValueColor = Settings.Display.Visuals.UniqueValueColor;
        Color currencyValueColor = Settings.Display.Visuals.CurrencyValueColor;
        Color failColor = Settings.Tracking.CountFailMapToStatistic
            ? new Color(220, 70, 70, 255)
            : textColor;
        var customTrackedItems = GetConfiguredCustomTrackedItems();
        var mapModifierSettings = Settings.Display.MapModifiers;

        var lines = new List<DisplayLine>();

        if (Settings.Display.Visibility.ShowHeader)
            lines.Add(new(new DisplaySegment("Map Drop Statistics", Settings.Display.Visuals.HeaderColor)));

        if (Settings.Display.Visibility.ShowArea)
            lines.Add(new(new DisplaySegment($"Area: {FormatAreaStatusText()}", textColor)));

        if (Settings.Display.Visibility.ShowMapsAndFails)
        {
            lines.Add(new(
                new DisplaySegment($"Maps: {_sessionStats.AreasTracked}", textColor),
                new DisplaySegment(" | ", textColor),
                new DisplaySegment("Fails: ", failColor),
                new DisplaySegment(_sessionStats.FailedAreas.ToString(CultureInfo.InvariantCulture), failColor)));

            if (_appliedAreaReviews.Count > 0)
                lines.Add(new(new DisplaySegment($"Last counted: {_appliedAreaReviews[^1].AreaName}", textColor)));
        }

        if (Settings.Display.Visibility.ShowMapTime)
        {
            lines.Add(new(true,
                new DisplaySegment("Map time: ", textColor),
                new DisplaySegment(FormatDuration(_currentAreaStats.Elapsed), currencyValueColor),
                new DisplaySegment(" | ", textColor),
                new DisplaySegment(FormatDuration(_sessionStats.AverageMapTime), currencyValueColor)));
        }

        if (Settings.Display.Visibility.ShowHoTime)
        {
            lines.Add(new(true,
                new DisplaySegment("HO time: ", textColor),
                new DisplaySegment(FormatDuration(_sessionStats.CurrentNonMapElapsed), currencyValueColor),
                new DisplaySegment(" | ", textColor),
                new DisplaySegment(FormatDuration(_sessionStats.TotalNonMapTime), currencyValueColor)));
        }

        if (Settings.Display.Visibility.ShowUniqueItems)
        {
            lines.Add(new(true,
                new DisplaySegment("Unique: ", textColor),
                new DisplaySegment(_currentAreaStats.UniqueItems.ToString(CultureInfo.InvariantCulture), uniqueValueColor),
                new DisplaySegment(" | ", textColor),
                new DisplaySegment(FormatAverageCeiling(_sessionStats.AverageUniqueItems), uniqueValueColor)));
        }

        if (Settings.Display.Visibility.ShowT0Uniques)
        {
            var lastT0Name = GetDisplayableT0Name();
            lines.Add(new(true,
                new DisplaySegment($"T0 {lastT0Name}: ", textColor),
                new DisplaySegment(_currentAreaStats.T0UniqueItems.ToString(CultureInfo.InvariantCulture), uniqueValueColor),
                new DisplaySegment(" | ", textColor),
                new DisplaySegment(FormatAverage(_sessionStats.AverageT0UniqueItems), uniqueValueColor)));
        }

        if (Settings.Display.Visibility.ShowCurrencyQuantity)
        {
            lines.Add(new(true,
                new DisplaySegment("Curr: ", textColor),
                new DisplaySegment(_currentAreaStats.CurrencyQuantity.ToString(CultureInfo.InvariantCulture), textColor),
                new DisplaySegment(" | ", textColor),
                new DisplaySegment(FormatAverageCeiling(_sessionStats.AverageCurrencyQuantity), textColor)));
        }

        if (Settings.Display.Visibility.ShowDivineOrbs)
        {
            lines.Add(new(true,
                new DisplaySegment("Divine Orbs: ", textColor),
                new DisplaySegment(_currentAreaStats.DivineOrbQuantity.ToString(CultureInfo.InvariantCulture), currencyValueColor),
                new DisplaySegment(" | ", textColor),
                new DisplaySegment(FormatAverage(_sessionStats.AverageDivineOrbQuantity), currencyValueColor)));
        }

        if (Settings.Display.Visibility.ShowValdosBox)
        {
            lines.Add(new(true,
                new DisplaySegment("Valdo's Box: ", textColor),
                new DisplaySegment(_currentAreaStats.ValdosPuzzleBoxQuantity.ToString(CultureInfo.InvariantCulture), currencyValueColor),
                new DisplaySegment(" | ", textColor),
                new DisplaySegment(FormatAverage(_sessionStats.AverageValdosPuzzleBoxQuantity), currencyValueColor)));
        }

        if (Settings.Display.Visibility.ShowCustomTrackedItems)
        {
            foreach (var itemName in customTrackedItems)
            {
                lines.Add(new(true,
                    new DisplaySegment($"{TruncateDisplayName(itemName, Settings.Display.Visibility.CustomTrackedItemMaxLabelLength)}: ", textColor),
                    new DisplaySegment(GetCurrentTrackedItemCount(itemName).ToString(CultureInfo.InvariantCulture), currencyValueColor),
                    new DisplaySegment(" | ", textColor),
                    new DisplaySegment(FormatAverage(_sessionStats.GetAverageCustomTrackedDrop(itemName)), currencyValueColor)));
            }
        }

        var mapModifierLines = BuildMapModifierLines(mapModifierSettings, textColor, currencyValueColor);
        if (mapModifierLines.Count > 0)
        {
            if (mapModifierSettings.ShowBlockHeader)
                lines.Add(new(new DisplaySegment("Area Modifiers", Settings.Display.Visuals.HeaderColor)));

            lines.AddRange(mapModifierLines);
        }

        if (Settings.Debug.EnableOverlay)
        {
            lines.Add(new(new DisplaySegment($"Dbg EA/PR: {_entityAddedAttempts}/{_pendingRetryAttempts}", textColor)));
            lines.Add(new(new DisplaySegment($"Dbg PendingNow: {_pendingDropKeys.Count}", textColor)));
        }

        return lines;
    }

    private void ResetDebugCounters()
    {
        _entityAddedAttempts = 0;
        _pendingRetryAttempts = 0;
    }

    private void DrawSavedMapStatsViewer()
    {
        ImGui.Separator();
        if (!ImGui.CollapsingHeader("Saved MapStats Viewer", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var saveMapStats = Settings.Actions.SaveMapStats.Value;
        if (ImGui.Checkbox("Save map stats", ref saveMapStats))
            Settings.Actions.SaveMapStats.Value = saveMapStats;

        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();

        var maxSavedMapStats = Settings.Actions.MaxSavedMapStats.Value;
        ImGui.SetNextItemWidth(140);
        if (ImGui.SliderInt("Max saved map stats", ref maxSavedMapStats, 1, 500))
        {
            Settings.Actions.MaxSavedMapStats.Value = maxSavedMapStats;
            PruneSavedMapStats();
            RefreshSavedMapStats();
        }

        if (ImGui.Button("Refresh Saved MapStats"))
            RefreshSavedMapStats();

        ImGui.SameLine();
        if (ImGui.Button("Clear Saved MapStats"))
            ClearSavedMapStats();

        ImGui.SameLine();
        if (ImGui.Button("Reset all statistics"))
            ResetSessionStats();

        ImGui.SameLine();
        ImGui.Text($"Saved: {_savedMapStatFiles.Count}");

        var hasAppliedMap = _appliedAreaReviews.Count > 0;
        var lastAppliedLabel = hasAppliedMap
            ? $"Last counted: {_appliedAreaReviews[^1].AreaName}"
            : "Last counted: none";
        ImGui.TextUnformatted(lastAppliedLabel);
        if (ImGui.Button("Undo last counted map"))
            UndoLastAppliedMap();

        ImGui.SameLine();
        if (!hasAppliedMap)
            ImGui.TextDisabled("Nothing to undo");

        ImGui.PushItemWidth(260);
        ImGui.InputTextWithHint("##savedMapStatsFilter", "Filter by filename or area", ref _savedMapStatsFilter, 200);
        ImGui.PopItemWidth();

        if (ImGui.BeginTable("SavedMapStatsViewer", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp, new Vector2(0, 360)))
        {
            ImGui.TableSetupColumn("Files", ImGuiTableColumnFlags.WidthStretch, 0.42f);
            ImGui.TableSetupColumn("Details", ImGuiTableColumnFlags.WidthStretch, 0.58f);
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            DrawSavedMapStatsFileList();

            ImGui.TableSetColumnIndex(1);
            DrawSavedMapStatsDetails();

            ImGui.EndTable();
        }
    }

    private void DrawTrackedDropWindow()
    {
        if (!Settings.TrackedDropWindow.Enable)
            return;

        if (Settings.TrackedDropWindow.ShowInHideoutOnly && !IsInHideout())
            return;

        var t0Entries = GetTrackedDropWindowEntries(isT0Group: true);
        var customEntries = GetTrackedDropWindowEntries(isT0Group: false);
        var hasMapCounter = _trackedDropWindowStats.TotalMaps > 0;
        if (!hasMapCounter && t0Entries.Count == 0 && customEntries.Count == 0)
            return;

        ImGui.SetNextWindowBgAlpha(Settings.Display.Visuals.BackgroundColor.Value.A / 255f);
        ImGui.SetNextWindowSize(new Vector2(320, 0), ImGuiCond.FirstUseEver);

        var windowFlags = ImGuiWindowFlags.AlwaysAutoResize;
        if (!ImGui.Begin("Global Statistics##MapDropStatistics", windowFlags))
        {
            ImGui.End();
            return;
        }

        if (Settings.TrackedDropWindow.ShowHeader)
            ImGui.TextColored(ToVector4(Settings.Display.Visuals.HeaderColor), "Global Statistics");

        if (ImGui.Button("Reset All"))
            ResetTrackedDropWindowAllStats();

        ImGui.SameLine();
        if (ImGui.Button("Reset Session"))
            ResetTrackedDropWindowSessionStats();

        if (hasMapCounter)
            DrawTrackedDropWindowCounterRow("Maps", _trackedDropWindowStats.TotalMaps, _trackedDropWindowStats.SessionMaps);

        DrawTrackedDropWindowGroup("T0 uniques", t0Entries);
        DrawTrackedDropWindowGroup("Custom items", customEntries);

        ImGui.End();
    }

    private List<(string itemName, int totalCount, int sessionCount)> GetTrackedDropWindowEntries(bool isT0Group)
    {
        return _trackedDropWindowStats.TotalCounts
            .Where(x => x.Value > 0 && IsT0Unique(x.Key) == isT0Group)
            .Select(x => (
                itemName: x.Key,
                totalCount: x.Value,
                sessionCount: _trackedDropWindowStats.SessionCounts.TryGetValue(x.Key, out var sessionCount) ? sessionCount : 0))
            .OrderByDescending(x => x.totalCount)
            .ThenBy(x => x.itemName, StringComparer.InvariantCultureIgnoreCase)
            .ToList();
    }

    private void DrawTrackedDropWindowGroup(string groupName, List<(string itemName, int totalCount, int sessionCount)> entries)
    {
        if (entries.Count == 0)
            return;

        ImGui.Separator();
        ImGui.TextColored(ToVector4(Settings.Display.Visuals.HeaderColor), groupName);

        foreach (var (itemName, totalCount, sessionCount) in entries)
            DrawTrackedDropWindowCounterRow(itemName, totalCount, sessionCount);
    }

    private void DrawTrackedDropWindowCounterRow(string label, int totalCount, int sessionCount)
    {
        ImGui.TextColored(ToVector4(Settings.Display.Visuals.TextColor), TruncateDisplayName(label, Settings.TrackedDropWindow.MaxLabelLength));
        ImGui.SameLine();
        ImGui.TextColored(ToVector4(Settings.Display.Visuals.CurrencyValueColor), totalCount.ToString(CultureInfo.InvariantCulture));
        ImGui.SameLine();
        ImGui.TextColored(ToVector4(Settings.Display.Visuals.TextColor), "|");
        ImGui.SameLine();
        ImGui.TextColored(ToVector4(Settings.TrackedDropWindow.SessionValueColor), $"+{sessionCount.ToString(CultureInfo.InvariantCulture)}");
    }

    private void ResetTrackedDropWindowAllStats()
    {
        _trackedDropWindowStats.ResetAll();
        SaveSessionSnapshot();
    }

    private void ResetTrackedDropWindowSessionStats()
    {
        _trackedDropWindowStats.ResetSession();
        SaveSessionSnapshot();
    }

    private static System.Numerics.Vector4 ToVector4(Color color)
    {
        return new System.Numerics.Vector4(
            color.R / 255f,
            color.G / 255f,
            color.B / 255f,
            color.A / 255f);
    }

    private void DrawSavedMapStatsFileList()
    {
        var opened = ImGui.BeginChild("SavedMapStatsFiles");
        if (!opened)
        {
            ImGui.EndChild();
            return;
        }

        var filter = _savedMapStatsFilter?.Trim() ?? string.Empty;
        foreach (var filePath in _savedMapStatFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            if (!string.IsNullOrWhiteSpace(filter) &&
                fileName.Contains(filter, StringComparison.InvariantCultureIgnoreCase) == false)
            {
                if (!SavedMapStatMatchesAreaFilter(filePath, filter))
                    continue;
            }

            var isSelected = string.Equals(_selectedSavedMapStatPath, filePath, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable($"{fileName}##{filePath}", isSelected))
                LoadSavedMapStat(filePath);
        }

        ImGui.EndChild();
    }

    private void DrawSavedMapStatsDetails()
    {
        var opened = ImGui.BeginChild("SavedMapStatsDetails");
        if (!opened)
        {
            ImGui.EndChild();
            return;
        }

        if (_selectedAreaDump == null)
        {
            ImGui.TextDisabled("Select a saved map stat on the left.");
            ImGui.EndChild();
            return;
        }

        ImGui.Text($"Area: {_selectedAreaDump.AreaName}");
        ImGui.Text($"Saved: {_selectedAreaDump.SavedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        ImGui.Text($"Unique: {_selectedAreaDump.UniqueItems}");
        ImGui.Text($"T0: {_selectedAreaDump.T0UniqueItems}");
        ImGui.Text($"Currency qty: {_selectedAreaDump.CurrencyQuantity}");
        ImGui.Text($"Fragments qty: {_selectedAreaDump.FragmentQuantity}");
        ImGui.Text($"Start IQ / Final IQ: {_selectedAreaDump.StartItemQuantity} / {_selectedAreaDump.FinalItemQuantity}");
        ImGui.Text($"Start IR / Final IR: {_selectedAreaDump.StartItemRarity} / {_selectedAreaDump.FinalItemRarity}");
        ImGui.Text($"Pack / mCur / mMap / mScar: {_selectedAreaDump.FinalPackSize} / {_selectedAreaDump.FinalMoreCurrency} / {_selectedAreaDump.FinalMoreMaps} / {_selectedAreaDump.FinalMoreScarabs}");
        ImGui.Text($"Bases N/M/R: {_selectedAreaDump.NormalBaseItems}/{_selectedAreaDump.MagicBaseItems}/{_selectedAreaDump.RareBaseItems}");
        ImGui.Text($"Divine: {_selectedAreaDump.DivineOrbQuantity}");
        ImGui.Text($"Valdo: {_selectedAreaDump.ValdosPuzzleBoxQuantity}");

        var customTracked = _selectedAreaDump.CustomTracked ?? new Dictionary<string, int>(StringComparer.InvariantCultureIgnoreCase);
        if (customTracked.Count > 0)
        {
            ImGui.Separator();
            ImGui.Text("Custom tracked:");
            foreach (var entry in customTracked.OrderBy(x => x.Key, StringComparer.InvariantCultureIgnoreCase))
                ImGui.Text($"{entry.Key}: {entry.Value}");
        }

        ImGui.Separator();
        ImGui.Text("Drops:");
        if (ImGui.BeginTable("SavedMapStatsDrops", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Drop");
            ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableHeadersRow();

            foreach (var entry in (_selectedAreaDump.Drops ?? new Dictionary<string, int>(StringComparer.InvariantCultureIgnoreCase))
                         .OrderByDescending(x => x.Value)
                         .ThenBy(x => x.Key, StringComparer.InvariantCultureIgnoreCase))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(entry.Key);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(entry.Value.ToString(CultureInfo.InvariantCulture));
            }

            ImGui.EndTable();
        }

        ImGui.EndChild();
    }

    private string GetAreaStatusText()
    {
        if (_trackingCurrentArea)
            return _currentAreaName;

        return $"{_currentAreaName} (not tracked)";
    }

    private string FormatAreaStatusText()
    {
        var areaText = GetAreaStatusText();
        if (string.IsNullOrWhiteSpace(areaText))
            return "Unknown";

        areaText = areaText
            .Replace(" Hideout", " HO", StringComparison.InvariantCultureIgnoreCase)
            .Replace("(not tracked)", "(idle)", StringComparison.InvariantCultureIgnoreCase);

        const int maxLength = 28;
        if (areaText.Length <= maxLength)
            return areaText;

        return $"{areaText[..(maxLength - 1)]}…";
    }

    private bool SavedMapStatMatchesAreaFilter(string filePath, string filter)
    {
        try
        {
            var dump = JsonSerializer.Deserialize<AreaDump>(File.ReadAllText(filePath), JsonOptions);
            return dump?.AreaName?.Contains(filter, StringComparison.InvariantCultureIgnoreCase) == true;
        }
        catch
        {
            return false;
        }
    }

    private string GetDisplayableT0Name()
    {
        var name = !string.IsNullOrWhiteSpace(_currentAreaStats.LastT0UniqueName)
            ? _currentAreaStats.LastT0UniqueName
            : _sessionStats.LastT0UniqueName;

        return TruncateDisplayName(string.IsNullOrWhiteSpace(name) ? "-" : name, 18);
    }

    private static string TruncateDisplayName(string value, int maxLength = 16)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var trimmed = value.Trim();
        if (trimmed.Length <= maxLength)
            return trimmed;

        return $"{trimmed[..Math.Max(maxLength - 3, 1)]}...";
    }

    private static bool IsSaveLockedArea(string areaName)
    {
        return string.Equals(areaName?.Trim(), SaveLockedAreaName, StringComparison.InvariantCultureIgnoreCase);
    }

    private static string FormatAverage(double value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string FormatAverageCeiling(double value)
    {
        return Math.Ceiling(value).ToString("0", CultureInfo.InvariantCulture);
    }

    private List<DisplayLine> BuildMapModifierLines(MapModifierDisplaySettings settings, Color textColor, Color valueColor)
    {
        var lines = new List<DisplayLine>();

        if (settings.ShowItemRarityStats)
        {
            lines.Add(BuildItemModifierLine(
                "ir",
                _currentAreaStats.CurrentItemRarity,
                _currentAreaStats.CurrentItemRarity - _currentAreaStats.StartItemRarity,
                settings.ShowStartAverageForQuantityAndRarity ? FormatAverageCeiling(_sessionStats.AverageStartItemRarity) : string.Empty,
                settings.ShowFinalAverageForQuantityAndRarity ? FormatAverageCeiling(_sessionStats.AverageFinalItemRarity) : string.Empty,
                textColor,
                valueColor));
        }

        if (settings.ShowItemQuantityStats)
        {
            lines.Add(BuildItemModifierLine(
                "iq",
                _currentAreaStats.CurrentItemQuantity,
                _currentAreaStats.CurrentItemQuantity - _currentAreaStats.StartItemQuantity,
                settings.ShowStartAverageForQuantityAndRarity ? FormatAverageCeiling(_sessionStats.AverageStartItemQuantity) : string.Empty,
                settings.ShowFinalAverageForQuantityAndRarity ? FormatAverageCeiling(_sessionStats.AverageFinalItemQuantity) : string.Empty,
                textColor,
                valueColor));
        }

        if (settings.ShowPackSizeStats)
            lines.Add(BuildSimpleModifierLine("pack", _currentAreaStats.CurrentPackSize, FormatAverageCeiling(_sessionStats.AverageFinalPackSize), textColor, valueColor));

        if (settings.ShowMoreCurrencyStats)
            lines.Add(BuildSimpleModifierLine("cur", _currentAreaStats.CurrentMoreCurrency, FormatAverageCeiling(_sessionStats.AverageFinalMoreCurrency), textColor, valueColor));

        if (settings.ShowMoreScarabsStats)
            lines.Add(BuildSimpleModifierLine("scarb", _currentAreaStats.CurrentMoreScarabs, FormatAverageCeiling(_sessionStats.AverageFinalMoreScarabs), textColor, valueColor));

        if (settings.ShowMoreMapsStats)
            lines.Add(BuildSimpleModifierLine("map", _currentAreaStats.CurrentMoreMaps, FormatAverageCeiling(_sessionStats.AverageFinalMoreMaps), textColor, valueColor));

        return lines;
    }

    private DisplayLine BuildItemModifierLine(string label, int currentValue, int deltaValue, string averageStartValue, string averageFinalValue, Color textColor, Color valueColor)
    {
        var segments = new List<DisplaySegment>
        {
            new($"{label}: ", textColor),
            new(currentValue.ToString(CultureInfo.InvariantCulture), valueColor),
            new(FormatSignedValue(deltaValue), deltaValue >= 0 ? Settings.TrackedDropWindow.SessionValueColor : new Color(220, 80, 80, 255))
        };

        if (!string.IsNullOrWhiteSpace(averageStartValue))
        {
            segments.Add(new("|", textColor));
            segments.Add(new(averageStartValue, valueColor));
        }

        if (!string.IsNullOrWhiteSpace(averageFinalValue))
        {
            segments.Add(new("|", textColor));
            segments.Add(new(averageFinalValue, valueColor));
        }

        return new DisplayLine(true, 1, segments.ToArray());
    }

    private static DisplayLine BuildSimpleModifierLine(string label, int currentValue, string averageValue, Color textColor, Color valueColor)
    {
        return new DisplayLine(true, 1,
            new DisplaySegment($"{label}: ", textColor),
            new DisplaySegment(currentValue.ToString(CultureInfo.InvariantCulture), valueColor),
            new DisplaySegment("|", textColor),
            new DisplaySegment(averageValue, valueColor));
    }

    private static string FormatSignedValue(int value)
    {
        return value >= 0
            ? $"+{value.ToString(CultureInfo.InvariantCulture)}"
            : value.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;

        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private bool ShouldDraw()
    {
        var ingameUi = GameController?.IngameState?.IngameUi;
        if (ingameUi == null)
            return false;

        if (!Settings.Display.Panels.RenderOnFullPanels && ingameUi.FullscreenPanels?.Any(x => x.IsVisible) == true)
            return false;

        if (!Settings.Display.Panels.RenderOnLargePanels && ingameUi.LargePanels?.Any(x => x.IsVisible) == true)
            return false;

        if (!Settings.Display.Panels.RenderOnLeftPanels && ingameUi.OpenLeftPanel?.IsVisible == true)
            return false;

        return Settings.Display.Panels.RenderOnRightPanels || ingameUi.OpenRightPanel?.IsVisible != true;
    }

    private void DrawOverlay(List<DisplayLine> lines)
    {
        if (lines.Count == 0)
            return;

        var windowRect = GameController.Window.GetWindowRectangle();
        var anchor = new Vector2(
            windowRect.Width * (Settings.Display.Position.XPos / 100f),
            windowRect.Height * (Settings.Display.Position.YPos / 100f));

        var columnLayouts = BuildColumnLayouts(lines);
        var measuredLines = new List<(DisplayLine line, Vector2 size)>(lines.Count);
        foreach (var line in lines)
        {
            var textSize = MeasureLine(line, columnLayouts);
            measuredLines.Add((line, textSize));
        }

        var currentY = anchor.Y;
        var pad = Settings.Display.Visuals.BorderPadding;
        var minX = anchor.X;
        var maxX = anchor.X;
        var minY = anchor.Y;
        var maxY = anchor.Y;

        var drawData = new List<(DisplayLine line, Vector2 position)>(measuredLines.Count);
        foreach (var (line, size) in measuredLines)
        {
            var position = new Vector2(anchor.X, currentY);
            drawData.Add((line, position));
            currentY += size.Y + Settings.Display.Visuals.TextSpacing;

            maxX = Math.Max(maxX, position.X + size.X);
            maxY = Math.Max(maxY, position.Y + size.Y);
        }

        var boxRect = new RectangleF(minX - pad, minY - pad, (maxX - minX) + (pad * 2), (maxY - minY) + (pad * 2));
        Graphics.DrawBox(boxRect, Settings.Display.Visuals.BackgroundColor, Settings.Display.Visuals.BorderRounding);
        Graphics.DrawFrame(
            boxRect,
            Settings.Display.Visuals.BorderColor,
            Settings.Display.Visuals.BorderRounding,
            Settings.Display.Visuals.BorderThickness,
            (int)ImDrawFlags.RoundCornersAll);

        foreach (var (line, position) in drawData)
            DrawLine(line, position, columnLayouts);
    }

    private Dictionary<int, ColumnLayout> BuildColumnLayouts(List<DisplayLine> lines)
    {
        var layouts = new Dictionary<int, ColumnLayout>();
        foreach (var group in lines.Where(x => x.AlignColumns).GroupBy(x => x.AlignmentGroup))
        {
            var maxColumns = group.Max(x => x.Segments.Length);
            var widths = new float[maxColumns];
            foreach (var line in group)
            {
                for (var index = 0; index < line.Segments.Length; index++)
                    widths[index] = Math.Max(widths[index], MeasureText(line.Segments[index].Text).X + (index < line.Segments.Length - 1 ? 14f : 0f));
            }

            layouts[group.Key] = new ColumnLayout(widths);
        }

        return layouts;
    }

    private Vector2 MeasureLine(DisplayLine line, Dictionary<int, ColumnLayout> layouts)
    {
        if (line.AlignColumns && layouts.TryGetValue(line.AlignmentGroup, out var layout))
        {
            var height = line.Segments
                .Select(x => MeasureText(x.Text).Y)
                .DefaultIfEmpty(0f)
                .Max();

            return new Vector2(layout.TotalWidth, height);
        }

        var totalWidth = 0f;
        var maxHeight = 0f;

        foreach (var segment in line.Segments)
        {
            var textSize = MeasureText(segment.Text);
            totalWidth += textSize.X;
            maxHeight = Math.Max(maxHeight, textSize.Y);
        }

        return new Vector2(totalWidth, maxHeight);
    }

    private void DrawLine(DisplayLine line, Vector2 position, Dictionary<int, ColumnLayout> layouts)
    {
        if (line.AlignColumns && layouts.TryGetValue(line.AlignmentGroup, out var layout))
        {
            DrawAlignedLine(line, position, layout);
            return;
        }

        var currentX = position.X;
        foreach (var segment in line.Segments)
        {
            var segmentPosition = new Vector2(currentX, position.Y);
            if (Settings.Display.Visuals.UseCustomFont)
            {
                Graphics.DrawText(segment.Text, segmentPosition, segment.Color, Settings.Display.Visuals.CustomLoadedFont, FontAlign.Left);
                currentX += MeasureText(segment.Text).X;
            }
            else
            {
                Graphics.DrawText(segment.Text, segmentPosition, segment.Color, FontAlign.Left);
                currentX += MeasureText(segment.Text).X;
            }
        }
    }

    private void DrawAlignedLine(DisplayLine line, Vector2 position, ColumnLayout layout)
    {
        var currentX = position.X;
        for (var index = 0; index < line.Segments.Length; index++)
        {
            DrawTextSegment(line.Segments[index], new Vector2(currentX, position.Y));
            currentX += index < layout.ColumnWidths.Length ? layout.ColumnWidths[index] : MeasureText(line.Segments[index].Text).X;
        }
    }

    private void DrawTextSegment(DisplaySegment segment, Vector2 position)
    {
        if (Settings.Display.Visuals.UseCustomFont)
            Graphics.DrawText(segment.Text, position, segment.Color, Settings.Display.Visuals.CustomLoadedFont, FontAlign.Left);
        else
            Graphics.DrawText(segment.Text, position, segment.Color, FontAlign.Left);
    }

    private Vector2 MeasureText(string text)
    {
        return Settings.Display.Visuals.UseCustomFont
            ? Graphics.MeasureText(text, Settings.Display.Visuals.CustomLoadedFont)
            : Graphics.MeasureText(text);
    }

}
