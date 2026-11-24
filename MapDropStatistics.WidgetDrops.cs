using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace MapDropStatistics;

public partial class MapDropStatistics
{
    private const double WidgetValuableDropDivine = 10;
    private const int WidgetDropDurationMs = 8000;
    private const int WidgetDropQuoteWindowMs = 12000;
    private const int MaxPendingWidgetDropQuotes = 256;
    private const int MaxQueuedWidgetDrops = 3;
    private const string WidgetDropTexture = "MapDropStatistics.WidgetDrop";
    private NinjaItemIcons _itemIcons;
    private WidgetDropIconCache _widgetDropIcons;
    private readonly object _widgetDropGate = new();
    private readonly Dictionary<(bool InMirage, uint EntityId), PendingWidgetDrop> _pendingWidgetDropQuotes = [];
    private readonly HashSet<(bool InMirage, uint EntityId)> _announcedWidgetDrops = [];
    private readonly ConcurrentQueue<WidgetDropAlert> _widgetDropAlerts = new();
    private WidgetDropAlert _shownWidgetDrop;
    private long _widgetDropShownAt;
    private WidgetDropIcon _widgetDropTextureAsset;
    private bool _widgetDropTextureReady;

    private sealed record PendingWidgetDrop(LootKey Item, string ArtPath, long SeenAt);
    private sealed record WidgetDropAlert(Guid RunId, string Name, bool IsT0, double? UnitDivine,
        Task<WidgetDropIcon> Icon, long QueuedAt);

    private void ResetWidgetDrops()
    {
        lock (_widgetDropGate)
        {
            _pendingWidgetDropQuotes.Clear();
            _announcedWidgetDrops.Clear();
            _widgetDropAlerts.Clear();
        }
    }

    private void ObserveWidgetDrop(Entity world, Entity item, LootKey key)
    {
        var id = (_inMirage, world.Id);
        lock (_widgetDropGate)
        {
            _pendingWidgetDropQuotes.Remove(id);
            if (_announcedWidgetDrops.Contains(id))
                return;

            var isT0 = key.Category == "Unique" && IsT0Unique(key.Name);
            var quotes = _market?.Quotes;
            var unitDivine = quotes?.FindLoot(key.Name, key.Category, key.MapTier);
            if (isT0 || IsValuableWidgetDrop(unitDivine))
                QueueWidgetDrop(id, key.Name, item.GetComponent<RenderItem>()?.ResourcePath, isT0, unitDivine);
            else if (quotes == null && _pendingWidgetDropQuotes.Count < MaxPendingWidgetDropQuotes &&
                     key.Category is not ("Magic" or "Rare"))
                _pendingWidgetDropQuotes[id] = new(key, item.GetComponent<RenderItem>()?.ResourcePath, Environment.TickCount64);
        }
    }

    private static bool IsValuableWidgetDrop(double? unitDivine) => unitDivine is > WidgetValuableDropDivine &&
        double.IsFinite(unitDivine.Value);

    private void ResolvePendingWidgetDrops(long now)
    {
        var quotes = _market?.Quotes;
        if (quotes == null)
            return;

        lock (_widgetDropGate)
        {
            foreach (var (id, drop) in _pendingWidgetDropQuotes)
            {
                if (now - drop.SeenAt > WidgetDropQuoteWindowMs)
                    continue;
                var price = quotes.FindLoot(drop.Item.Name, drop.Item.Category, drop.Item.MapTier);
                if (IsValuableWidgetDrop(price))
                    QueueWidgetDrop(id, drop.Item.Name, drop.ArtPath, isT0: false, price);
            }
            _pendingWidgetDropQuotes.Clear();
        }
    }

    private void QueueWidgetDrop((bool InMirage, uint EntityId) id, string name, string artPath, bool isT0, double? unitDivine)
    {
        var run = _activeRun;
        if (run == null || _history == null || _history.IsDeleted(run.Id, run.SessionId) ||
            _history.GetSummary(run.Id)?.ExcludedFromStatistics == true ||
            (!isT0 && _history.IsIgnoredItem(name)) || !_announcedWidgetDrops.Add(id))
            return;

        if (string.IsNullOrWhiteSpace(artPath) && isT0)
            artPath = _uniqueArtMapping.FirstOrDefault(entry => entry.Value.Contains(name, StringComparer.OrdinalIgnoreCase)).Key;
        var league = run.League ?? GameController.IngameState?.ServerData?.League;
        if (_widgetDropAlerts.Count >= MaxQueuedWidgetDrops)
            _widgetDropAlerts.TryDequeue(out _);
        _widgetDropAlerts.Enqueue(new(run.Id, name, isT0, unitDivine, _widgetDropIcons?.Get(name, artPath, league),
            Environment.TickCount64));
    }

    private WidgetDropAlert GetWidgetDropToShow(long now)
    {
        var runId = _activeRun?.Id;
        if (_shownWidgetDrop is { } current && current.RunId == runId && now - _widgetDropShownAt < WidgetDropDurationMs)
            return current;

        _shownWidgetDrop = null;
        while (_widgetDropAlerts.TryDequeue(out var candidate))
        {
            if (candidate.RunId != runId || now - candidate.QueuedAt >= 3 * WidgetDropDurationMs)
                continue;
            _widgetDropShownAt = now;
            return _shownWidgetDrop = candidate;
        }
        return null;
    }

    private void DrawWidgetDrop(WidgetDropAlert alert, RectangleF bounds, long now, float lineHeight)
    {
        var elapsed = now - _widgetDropShownAt;
        var fade = Math.Clamp((WidgetDropDurationMs - elapsed) / 450f, 0, 1);
        var pulse = (0.72 + 0.28 * Math.Cos(Math.Tau * elapsed / 1500)) * fade;
        var accent = alert.IsT0 ? 0xF4B6D5 : 0xFFCE78;
        var frame = WidgetAccent(accent, (byte)(255 * pulse));
        Graphics.DrawBox(bounds, new Color(22, 22, 20, 242), 8);
        Graphics.DrawFrame(bounds, frame, 8, 2, (int)ImGuiNET.ImDrawFlags.RoundCornersAll);

        var priceLabel = alert.UnitDivine is > 0 and var price && double.IsFinite(price)
            ? price.ToString("0.##", CultureInfo.InvariantCulture) + " div"
            : null;
        var detail = alert.IsT0 && priceLabel != null ? $"T0 · {priceLabel}" : priceLabel ?? "T0 Unique";
        var centerX = bounds.X + bounds.Width / 2;
        var captionY = bounds.Bottom - 12 - 2 * lineHeight;
        if (alert.Icon is { IsCompletedSuccessfully: true, Result: { } icon })
        {
            if (_widgetDropTextureAsset != icon)
            {
                ClearWidgetDropTexture();
                _widgetDropTextureAsset = icon;
                _widgetDropTextureReady = Graphics.InitImage(WidgetDropTexture, icon.FilePath);
            }

            if (_widgetDropTextureReady)
            {
                var imageHeight = Math.Max(16, captionY - bounds.Y - 24);
                var scale = Math.Min((bounds.Width - 32) / icon.Width, imageHeight / icon.Height);
                var width = icon.Width * scale;
                var height = icon.Height * scale;
                var iconBounds = new RectangleF(centerX - width / 2, bounds.Y + 12 + (imageHeight - height) / 2, width, height);
                Graphics.DrawImage(WidgetDropTexture, iconBounds, new Color((byte)255, (byte)255, (byte)255, (byte)(255 * fade)));
            }
        }
        else
        {
            ClearWidgetDropTexture();
        }

        if (!_widgetDropTextureReady)
            captionY = bounds.Y + bounds.Height / 2 - lineHeight;
        using var clip = Graphics.BeginRectClip(new RectangleF(bounds.X + 8, bounds.Y, bounds.Width - 16, bounds.Height));
        var text = ClipWidgetText(alert.Name);
        Graphics.DrawText(text, new Vector2(Math.Max(bounds.X + 8, centerX - Graphics.MeasureText(text).X / 2), captionY),
            WidgetAccent(0xFFF4E1, (byte)(255 * fade)), FontAlign.Left);
        Graphics.DrawText(detail, new Vector2(centerX, captionY + lineHeight), WidgetAccent(accent, (byte)(255 * fade)), FontAlign.Center);
    }

    private void ClearWidgetDropTexture()
    {
        if (_widgetDropTextureReady)
            Graphics.DisposeTexture(WidgetDropTexture);
        _widgetDropTextureReady = false;
        _widgetDropTextureAsset = null;
    }
}
