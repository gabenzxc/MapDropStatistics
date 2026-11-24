using System;
using System.Linq;
using ExileCore.Shared.Enums;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace MapDropStatistics;

public partial class MapDropStatistics
{
    private const float EncounterBarWidth = 56;
    private long _lastWidgetUpdate;
    private LiveWidget _measuredWidget;
    private WidgetLayout _widgetLayout;

    private sealed record WidgetLayout(WidgetRow[] Rows, float Width, float Height, float Padding, float LineHeight,
        float CurrentOffset, float SessionOffset, float InlineOffset);

    private WidgetSettings GetWidgetDefaults()
    {
        var visible = Settings.Display.Visibility;
        (string Id, bool Enabled)[] previous =
        [
            ("maps", visible.ShowMapsAndFails), ("map-time", visible.ShowMapTime), ("hideout-time", visible.ShowHoTime),
            ("currency", visible.ShowCurrencyQuantity), ("uniques", visible.ShowUniqueItems),
            ("t0", visible.ShowT0Uniques), ("last-t0", visible.ShowT0Uniques), ("divine", visible.ShowDivineOrbs),
            ("valdo", visible.ShowValdosBox), ("profit", visible.ShowMapProfit), ("custom", visible.ShowCustomTrackedItems)
        ];
        return new WidgetSettings(ShowOutsideMaps: Settings.Tracking.ShowWhenNotTrackingArea,
            X: (int)Settings.Display.Position.XPos.Value, Y: (int)Settings.Display.Position.YPos.Value,
            HiddenRows: previous.Where(row => !row.Enabled).Select(row => row.Id).ToArray(),
            CustomItems: GetConfiguredCustomTrackedItems().Concat(GetConfiguredTrackedDropWindowItems())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private void UpdateLiveWidget(bool inGame)
    {
        if (_history == null)
            return;
        var now = Environment.TickCount64;
        if (now - _lastWidgetUpdate < 1000)
            return;
        _lastWidgetUpdate = now;

        var area = inGame ? GameController.Area.CurrentArea : null;
        _history.ObserveHideout(area?.IsHideout == true);
        var session = _history.ActiveSessionId;
        var inMap = inGame && _trackingCurrentArea;
        var mapSeconds = inMap && _trackingCurrentArea && _currentAreaStartedUtc != default
            ? (DateTime.UtcNow - _currentAreaStartedUtc).TotalSeconds : (double?)null;
        var run = _activeRun;
        _history.PublishWidget(session, run?.Id, area?.Name ?? "Waiting for game", inGame, inMap, mapSeconds,
            run?.SessionId);
    }

    private bool CanDrawWidget(WidgetSettings settings)
    {
        var ui = GameController.IngameState.IngameUi;
        return settings.ShowOverPanels || ui != null &&
            ui.FullscreenPanels?.Any(panel => panel.IsVisible) != true &&
            ui.LargePanels?.Any(panel => panel.IsVisible) != true &&
            ui.OpenLeftPanel?.IsVisible != true && ui.OpenRightPanel?.IsVisible != true;
    }

    private void DrawLiveWidget()
    {
        var widget = _history?.ReadWidget();
        if (widget?.Data is not { InGame: true } data || !widget.Settings.Enabled ||
            (!data.InMap && !widget.Settings.ShowOutsideMaps) || !CanDrawWidget(widget.Settings))
        {
            ClearWidgetDropTexture();
            return;
        }

        if (widget != _measuredWidget)
        {
            _widgetLayout = MeasureWidget(widget);
            _measuredWidget = widget;
        }
        var layout = _widgetLayout;
        var window = GameController.Window.GetWindowRectangle();
        var now = Environment.TickCount64;
        var alert = data.InMap && !data.MapExcluded ? GetWidgetDropToShow(now) : null;
        var width = alert == null ? layout.Width : Math.Max(layout.Width, 180);
        var height = alert == null ? layout.Height : Math.Max(layout.Height, 144);
        var x = Math.Max(0, window.Width - width) * widget.Settings.X / 100f;
        var y = Math.Max(0, window.Height - height) * widget.Settings.Y / 100f;
        var bounds = new RectangleF(x, y, width, height);
        if (alert != null)
        {
            DrawWidgetDrop(alert, bounds, now, layout.LineHeight);
            return;
        }

        ClearWidgetDropTexture();
        var alpha = (byte)(255 * widget.Settings.Opacity / 100);
        Graphics.DrawBox(bounds, new Color((byte)19, (byte)25, (byte)21, alpha), 8);
        Graphics.DrawFrame(bounds, new Color(66, 79, 66, 255), 8, 1, (int)ImGuiNET.ImDrawFlags.RoundCornersAll);
        x += layout.Padding;
        y += layout.Padding;
        Graphics.DrawText(data.Area, new Vector2(x, y), new Color(235, 198, 132, 255), FontAlign.Left);
        y += layout.LineHeight;
        foreach (var row in layout.Rows)
        {
            Graphics.DrawText(row.Label, new Vector2(x, y), new Color(184, 195, 181, 255), FontAlign.Left);
            var currentOffset = row.Inline ? layout.InlineOffset : layout.CurrentOffset;
            var currentColor = row.Current == "—" ? new Color(125, 136, 123, 255) : WidgetAccent(row.AccentRgb, 255);
            Graphics.DrawText(row.Current, new Vector2(x + currentOffset, y), currentColor, FontAlign.Left);
            if (row.Progress is { } progress)
            {
                var bar = new RectangleF(x + layout.SessionOffset, y + (layout.LineHeight - 5) / 2, EncounterBarWidth, 5);
                Graphics.DrawBox(bar, new Color(49, 59, 51, 255), 2);
                var fill = EncounterBarWidth * Math.Clamp((float)progress.Completed / progress.Found, 0, 1);
                if (fill > 0)
                    Graphics.DrawBox(new RectangleF(bar.X, bar.Y, fill, bar.Height), WidgetAccent(row.AccentRgb, 255), 2);
            }
            else if (row.Session != null)
                Graphics.DrawText(row.Session, new Vector2(x + layout.SessionOffset, y), WidgetAccent(row.AccentRgb, 205), FontAlign.Left);
            y += layout.LineHeight;
        }
    }

    private WidgetLayout MeasureWidget(LiveWidget widget)
    {
        var rows = widget.Data.Rows.Where(row => widget.Settings.Shows(row.Id))
            .Select(row => row with { Label = ClipWidgetText(row.Label),
                Current = ClipWidgetText(row.Current) }).ToArray();
        var padding = widget.Settings.Compact ? 7f : 10f;
        var gap = widget.Settings.Compact ? 7f : 10f;
        var lineHeight = Graphics.MeasureText("Ag").Y + (widget.Settings.Compact ? 2 : 4);
        var regular = rows.Where(row => !row.Inline).ToArray();
        var inline = rows.FirstOrDefault(row => row.Inline);
        var labelWidth = regular.Select(row => Graphics.MeasureText(row.Label).X).DefaultIfEmpty().Max();
        var valueWidth = regular.Select(row => Graphics.MeasureText(row.Current).X).DefaultIfEmpty().Max();
        var sessionWidth = rows.Where(row => row.Session != null)
            .Select(row => Graphics.MeasureText(row.Session).X).DefaultIfEmpty().Max();
        if (rows.Any(row => row.Progress != null))
            sessionWidth = Math.Max(sessionWidth, EncounterBarWidth);
        var currentOffset = labelWidth + gap;
        var sessionOffset = currentOffset + valueWidth + gap;
        var inlineOffset = inline == null ? 0 : Graphics.MeasureText(inline.Label).X + gap;
        var inlineWidth = inline == null ? 0 : inlineOffset + Graphics.MeasureText(inline.Current).X;
        var rowWidth = sessionWidth > 0 ? sessionOffset + sessionWidth : currentOffset + valueWidth;
        var width = Math.Max(Graphics.MeasureText(widget.Data.Area).X, Math.Max(rowWidth, inlineWidth)) + 2 * padding;
        return new WidgetLayout(rows, width, (rows.Length + 1) * lineHeight + 2 * padding,
            padding, lineHeight, currentOffset, sessionOffset, inlineOffset);
    }

    private static string ClipWidgetText(string value) => value.Length <= 33 ? value : value[..32] + "…";

    private static Color WidgetAccent(int rgb, byte alpha) =>
        new((byte)((rgb >> 16) & 255), (byte)((rgb >> 8) & 255), (byte)(rgb & 255), alpha);
}
