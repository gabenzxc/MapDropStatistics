using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;

namespace MapDropStatistics;

internal sealed record PricingSourceStatus(string Message, string SnapshotDirectory, bool PluginMissing);

internal sealed class DashboardServer : IDisposable
{
    private static readonly Dictionary<string, (string Resource, string Type)> Assets = new()
    {
        ["/"] = ("index.html", "text/html; charset=utf-8"),
        ["/dashboard.css"] = ("dashboard.css", "text/css; charset=utf-8"),
        ["/dashboard.js"] = ("dashboard.js", "text/javascript; charset=utf-8"),
        ["/comparison.js"] = ("comparison.js", "text/javascript; charset=utf-8")
    };
    private static readonly (string Key, string Item)[] DropAverageMetricIcons =
    [
        ("stat:currency", "Chaos Orb"), ("stat:uniques", "Tabula Rasa"), ("stat:t0", "Mageblood"), ("stat:belts", "Meginord's Girdle")
    ];

    private readonly HttpListener _listener = new();
    private readonly MapHistory _history;
    private readonly Action<string> _logError;
    private readonly NinjaItemIcons _icons;
    private readonly Func<PricingSourceStatus> _pricingStatus;

    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}/";
    public bool IsListening => _listener.IsListening;

    public DashboardServer(int port, MapHistory history, Action<string> logError, NinjaItemIcons icons = null,
        Func<PricingSourceStatus> pricingStatus = null)
    {
        Port = port;
        _history = history;
        _logError = logError;
        _icons = icons;
        _pricingStatus = pricingStatus;
        try
        {
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Serve();
        }
        catch (HttpListenerException ex)
        {
            _logError($"Could not start Map Drop Statistics at {Url}: {ex.Message}");
        }
    }

    private async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException or InvalidOperationException)
            {
                return;
            }
            _ = Handle(context);
        }
    }

    private async Task Handle(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["Content-Security-Policy"] =
                "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' https://web.poecdn.com https://poe.ninja; base-uri 'none'; frame-ancestors 'none'";
            if (!string.Equals(request.UserHostName, $"127.0.0.1:{Port}", StringComparison.Ordinal) ||
                !IPAddress.IsLoopback(request.RemoteEndPoint.Address))
            {
                response.StatusCode = 403;
                return;
            }

            var path = request.Url.AbsolutePath;
            if (request.HttpMethod == "GET" && Assets.TryGetValue(path, out var asset))
            {
                using var resource = typeof(DashboardServer).Assembly.GetManifestResourceStream($"Dashboard.{asset.Resource}");
                if (resource == null)
                {
                    response.StatusCode = 404;
                    return;
                }
                response.ContentType = asset.Type;
                response.ContentLength64 = resource.Length;
                await resource.CopyToAsync(response.OutputStream).ConfigureAwait(false);
                return;
            }
            if (request.HttpMethod == "GET" && path == "/api/maps")
            {
                await SendJson(response, _history.List()).ConfigureAwait(false);
                return;
            }
            if (request.HttpMethod == "GET" && path == "/api/sessions")
            {
                await SendJson(response, _history.ListSessions()).ConfigureAwait(false);
                return;
            }
            if (request.HttpMethod == "GET" && path == "/api/pricing-status")
            {
                await SendJson(response, _pricingStatus?.Invoke()).ConfigureAwait(false);
                return;
            }
            if (request.HttpMethod == "GET" && path == "/api/widget")
            {
                await SendJson(response, _history.ReadWidgetForDashboard()).ConfigureAwait(false);
                return;
            }
            if (request.HttpMethod == "POST" && path == "/api/settings/widget")
            {
                if (!ValidateEdit(request, response, maxBodyBytes: 16384))
                    return;
                var settings = await JsonSerializer.DeserializeAsync<WidgetSettings>(request.InputStream, MapHistory.Json)
                    .ConfigureAwait(false);
                if (settings == null || settings.X is < 0 or > 100 || settings.Y is < 0 or > 100 ||
                    settings.Opacity is < 25 or > 100 ||
                    settings.HiddenRows?.Any(id => !WidgetSettings.Options.Any(option => option.Id == id)) == true ||
                    settings.CustomItems?.Any(name => string.IsNullOrWhiteSpace(name) ||
                        name.Length > 240 || name.Any(char.IsControl)) == true)
                {
                    response.StatusCode = 400;
                    return;
                }
                _history.SetWidgetSettings(settings with
                {
                    HiddenRows = settings.HiddenRows?.Distinct(StringComparer.Ordinal).ToArray(),
                    CustomItems = settings.CustomItems?.Select(name => name.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                });
                response.StatusCode = 204;
                return;
            }
            if (request.HttpMethod == "POST" && path == "/api/settings/map-costs")
            {
                if (!ValidateEdit(request, response))
                    return;
                var costs = await JsonSerializer.DeserializeAsync<MapCostSettings>(request.InputStream, MapHistory.Json)
                    .ConfigureAwait(false);
                if (costs == null || !IsValidMapPrice(costs.MapPriceDivine))
                {
                    response.StatusCode = 400;
                    return;
                }
                _history.SetMapCosts(costs);
                response.StatusCode = 204;
                return;
            }
            if (request.HttpMethod == "POST" && path == "/api/drop-averages")
            {
                if (!ValidateEdit(request, response, maxBodyBytes: 1024 * 1024))
                    return;
                var query = await JsonSerializer.DeserializeAsync<DropAverageQuery>(request.InputStream, MapHistory.Json)
                    .ConfigureAwait(false);
                if (query?.MapIds == null || query.ItemNames == null ||
                    query.ItemNames.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 240 || name.Any(char.IsControl)))
                {
                    response.StatusCode = 400;
                    return;
                }
                var averages = _history.GetDropAverages(query);
                if (_icons != null)
                {
                    var available = await _icons.Get(averages.League).ConfigureAwait(false);
                    var icons = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var (key, item) in DropAverageMetricIcons.Concat(averages.Drops.Keys.Select(name => ($"item:{name}", name))))
                    {
                        if (available.TryGetValue(item, out var icon))
                            icons[key] = icon;
                    }
                    averages = averages with { Icons = icons };
                }
                await SendJson(response, averages).ConfigureAwait(false);
                return;
            }
            if (request.HttpMethod == "POST" && path == "/api/unique-drops")
            {
                if (!ValidateEdit(request, response, maxBodyBytes: 1024 * 1024))
                    return;
                var query = await JsonSerializer.DeserializeAsync<UniqueDropsQuery>(request.InputStream, MapHistory.Json)
                    .ConfigureAwait(false);
                if (query?.MapIds == null)
                {
                    response.StatusCode = 400;
                    return;
                }
                var drops = _history.GetUniqueDrops(query);
                if (_icons != null && drops.Items.Length > 0)
                {
                    var icons = await _icons.Get(drops.League).ConfigureAwait(false);
                    drops = drops with { Items = drops.Items.Select(item => item with { Icon = icons.GetValueOrDefault(item.Name) }).ToArray() };
                }
                await SendJson(response, drops).ConfigureAwait(false);
                return;
            }
            if (request.HttpMethod == "POST" && path is "/api/sessions" or "/api/sessions/active" or "/api/sessions/rename" or "/api/sessions/delete" or
                "/api/settings/unique-prices" or "/api/settings/ignored-items" or "/api/settings/minimum-item-price")
            {
                if (!ValidateEdit(request, response))
                    return;
                if (path == "/api/sessions")
                    await SendJson(response, new { ActiveId = _history.StartNewSession() }).ConfigureAwait(false);
                else if (path == "/api/sessions/active")
                {
                    var id = await JsonSerializer.DeserializeAsync<Guid>(request.InputStream, MapHistory.Json).ConfigureAwait(false);
                    response.StatusCode = _history.ActivateSession(id) ? 204 : 404;
                }
                else if (path == "/api/sessions/rename")
                {
                    var rename = await JsonSerializer.DeserializeAsync<SessionRename>(request.InputStream, MapHistory.Json)
                        .ConfigureAwait(false);
                    var name = rename?.Name?.Trim();
                    if (name == null || name.Length is < 1 or > 80 || name.Any(char.IsControl) || rename.SessionId == Guid.Empty)
                    {
                        response.StatusCode = 400;
                        return;
                    }
                    response.StatusCode = _history.RenameSession(rename.SessionId, name) ? 204 : 404;
                }
                else if (path == "/api/sessions/delete")
                {
                    var id = await JsonSerializer.DeserializeAsync<Guid>(request.InputStream, MapHistory.Json).ConfigureAwait(false);
                    response.StatusCode = _history.DeleteSession(id) ? 204 : 404;
                }
                else if (path == "/api/settings/unique-prices")
                {
                    var include = await JsonSerializer.DeserializeAsync<bool>(request.InputStream, MapHistory.Json).ConfigureAwait(false);
                    _history.SetUniquePrices(include);
                    response.StatusCode = 204;
                }
                else if (path == "/api/settings/minimum-item-price")
                {
                    var minimum = await JsonSerializer.DeserializeAsync<MinimumItemPrice>(request.InputStream, MapHistory.Json).ConfigureAwait(false);
                    if (minimum != null && (!double.IsFinite(minimum.Value) || minimum.Value is < 0 or > 1000000 ||
                        minimum.Currency is not ("chaos" or "divine")))
                    {
                        response.StatusCode = 400;
                        return;
                    }
                    _history.SetMinimumPrice(minimum);
                    response.StatusCode = 204;
                }
                else
                {
                    var change = await JsonSerializer.DeserializeAsync<IgnoreItemChange>(request.InputStream, MapHistory.Json).ConfigureAwait(false);
                    var name = change?.Name?.Trim();
                    if (name == null || name.Length is < 1 or > 240 || name.Any(char.IsControl))
                    {
                        response.StatusCode = 400;
                        return;
                    }
                    _history.SetIgnoredItem(name, change.Ignored);
                    response.StatusCode = 204;
                }
                return;
            }
            if (request.HttpMethod == "GET" && path == "/api/session")
            {
                var selection = request.QueryString["session"];
                var pickup = request.QueryString["pickup"];
                if ((selection is not (null or "all" or "favorites") && !Guid.TryParse(selection, out _)) ||
                    pickup is not (null or "all" or "personal" or "party"))
                {
                    response.StatusCode = 400;
                    return;
                }
                var scope = pickup switch
                {
                    "personal" => LootScope.Personal,
                    "party" => LootScope.Party,
                    _ => LootScope.All
                };
                var id = selection switch
                {
                    null => (Guid?)null,
                    "all" or "favorites" => Guid.Empty,
                    _ => Guid.Parse(selection)
                };
                var session = _history.GetSessionDrops(id, scope, favoritesOnly: selection == "favorites");
                if (session == null)
                {
                    response.StatusCode = 404;
                    return;
                }
                if (_icons != null && session.Items.Length > 0)
                {
                    var icons = await _icons.Get(session.League).ConfigureAwait(false);
                    session = session with
                    {
                        Items = session.Items.Select(item => item with { Icon = icons.GetValueOrDefault(item.Name) }).ToArray()
                    };
                }
                await SendJson(response, session).ConfigureAwait(false);
                return;
            }

            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && parts[0] == "api" && parts[1] == "maps" && Guid.TryParse(parts[2], out var mapId))
            {
                if (request.HttpMethod == "GET" && parts.Length == 3 && _history.Get(mapId) is { } run)
                {
                    _history.AnnotateArchivedBelts(run);
                    if (_icons != null && run.Loot.Count > 0)
                    {
                        var icons = await _icons.Get(run.League).ConfigureAwait(false);
                        foreach (var item in run.Loot)
                            item.Icon = icons.GetValueOrDefault(item.Name);
                    }
                    await SendJson(response, run).ConfigureAwait(false);
                    return;
                }
                if (request.HttpMethod == "POST")
                {
                    await HandleEdit(request, response, parts, mapId).ConfigureAwait(false);
                    return;
                }
            }
            response.StatusCode = 404;
        }
        catch (JsonException)
        {
            response.StatusCode = 400;
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
        {
            // The browser disconnected or the plugin stopped listening.
        }
        catch (Exception ex)
        {
            response.StatusCode = 500;
            _logError($"Map history request failed: {ex.Message}");
        }
        finally
        {
            response.Close();
        }
    }

    private async Task HandleEdit(HttpListenerRequest request, HttpListenerResponse response, string[] parts, Guid mapId)
    {
        if (!ValidateEdit(request, response))
            return;

        if (parts.Length == 5 && parts[3] == "loot" && Guid.TryParse(parts[4], out var itemId))
        {
            var edit = await JsonSerializer.DeserializeAsync<LootEdit>(request.InputStream, MapHistory.Json).ConfigureAwait(false);
            if (edit == null || (edit.UnitDivine.HasValue &&
                (!double.IsFinite(edit.UnitDivine.Value) || edit.UnitDivine.Value is < 0 or > 1000000)))
            {
                response.StatusCode = 400;
                return;
            }
            response.StatusCode = _history.EditLoot(mapId, itemId, edit) ? 204 : 404;
            return;
        }
        if (parts.Length == 4 && parts[3] == "failure")
        {
            var failed = await JsonSerializer.DeserializeAsync<bool?>(request.InputStream, MapHistory.Json).ConfigureAwait(false);
            response.StatusCode = _history.EditFailure(mapId, failed) ? 204 : 404;
            return;
        }
        if (parts.Length == 4 && parts[3] == "statistics-exclusion")
        {
            var excluded = await JsonSerializer.DeserializeAsync<bool>(request.InputStream, MapHistory.Json).ConfigureAwait(false);
            response.StatusCode = _history.EditStatisticsExclusion(mapId, excluded) ? 204 : 404;
            return;
        }
        if (parts.Length == 4 && parts[3] == "favorite")
        {
            var favorite = await JsonSerializer.DeserializeAsync<bool>(request.InputStream, MapHistory.Json).ConfigureAwait(false);
            response.StatusCode = _history.EditFavorite(mapId, favorite) ? 204 : 404;
            return;
        }
        if (parts.Length == 4 && parts[3] == "delete")
        {
            var confirmed = await JsonSerializer.DeserializeAsync<bool>(request.InputStream, MapHistory.Json).ConfigureAwait(false);
            response.StatusCode = !confirmed ? 400 : _history.DeleteMap(mapId) ? 204 : 404;
            return;
        }
        if (parts.Length == 4 && parts[3] == "map-price")
        {
            var price = await JsonSerializer.DeserializeAsync<double?>(request.InputStream, MapHistory.Json).ConfigureAwait(false);
            if (price is { } value && !IsValidMapPrice(value))
            {
                response.StatusCode = 400;
                return;
            }
            response.StatusCode = _history.EditMapPrice(mapId, price) ? 204 : 404;
            return;
        }
        response.StatusCode = 404;
    }

    private static bool IsValidMapPrice(double price) => double.IsFinite(price) && price is >= 0 and <= 1000000;

    private bool ValidateEdit(HttpListenerRequest request, HttpListenerResponse response, int maxBodyBytes = 1024)
    {
        if (!string.Equals(request.Headers["Origin"], Url.TrimEnd('/'), StringComparison.Ordinal) ||
            !string.Equals(request.ContentType?.Split(';', 2)[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = 403;
            return false;
        }
        if (request.ContentLength64 < 1 || request.ContentLength64 > maxBodyBytes)
        {
            response.StatusCode = 400;
            return false;
        }
        return true;
    }

    private static async Task SendJson<T>(HttpListenerResponse response, T value)
    {
        response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(response.OutputStream, value, MapHistory.Json).ConfigureAwait(false);
    }

    public void Dispose() => _listener.Close();
}
