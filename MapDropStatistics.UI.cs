using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using ImGuiNET;
using Vector2 = System.Numerics.Vector2;

namespace MapDropStatistics;

public partial class MapDropStatistics
{
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
        if (ImGui.SliderInt("Max saved map stats", ref maxSavedMapStats, 1, ActionSettings.MaximumSavedMapStats))
        {
            Settings.Actions.MaxSavedMapStats.Value = maxSavedMapStats;
            RefreshSavedMapStats(prune: true);
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

    private static bool IsSaveLockedArea(string areaName)
    {
        return string.Equals(areaName?.Trim(), SaveLockedAreaName, StringComparison.InvariantCultureIgnoreCase);
    }
}
