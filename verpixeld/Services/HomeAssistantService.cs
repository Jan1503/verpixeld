using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CanvasManagement.Interfaces;
using verpixeld.Configuration;

namespace verpixeld.Services;

/// <summary>
///     Maintains a live connection to Home Assistant over its WebSocket API and mirrors entity states into
///     <see cref="HomeAssistantBridge" /> so extensions can read them in-process. Authenticates with a
///     long-lived token, pulls an initial snapshot (get_states) and then follows state_changed events.
///     Reconnects automatically with backoff.
/// </summary>
public sealed class HomeAssistantService
{
    private const int HistoryHours = 6;
    private readonly object _optionsLock = new();
    private HomeAssistantOptions _options;
    private CancellationTokenSource? _cts;
    private HttpClient? _http;
    private Task? _loop;
    private Task? _seedLoop;

    public HomeAssistantService(HomeAssistantOptions options)
    {
        _options = options;
    }

    public void Start()
    {
        HomeAssistantOptions options;
        lock (_optionsLock) options = Clone(_options);

        if (!options.Enabled)
        {
            Console.WriteLine("[HA] Disabled (set HomeAssistant:Enabled = true to enable)");
            return;
        }

        if (string.IsNullOrWhiteSpace(options.Token))
        {
            Console.WriteLine("[HA] No token configured — skipping Home Assistant connection");
            return;
        }

        _cts = new CancellationTokenSource();

        // REST client for History API seeding (token in the Authorization header; tolerate self-signed certs).
        _http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true })
        {
            BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/")
        };
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {options.Token}");

        _loop = Task.Run(() => RunAsync(_cts.Token));
        _seedLoop = Task.Run(() => SeedLoopAsync(_cts.Token));
        Console.WriteLine($"[HA] Home Assistant client starting ({options.BaseUrl})");
    }

    /// <summary>Live snapshot for the settings UI (token included so it can be edited).</summary>
    public HomeAssistantOptions Snapshot()
    {
        lock (_optionsLock) return Clone(_options);
    }

    /// <summary>Apply new connection settings and reconnect immediately (or disconnect if disabled).</summary>
    public void Apply(HomeAssistantOptions next)
    {
        lock (_optionsLock)
        {
            _options.Enabled = next.Enabled;
            _options.BaseUrl = string.IsNullOrWhiteSpace(next.BaseUrl) ? _options.BaseUrl : next.BaseUrl.Trim();
            _options.Token = next.Token ?? "";
        }

        Stop();
        Start();
    }

    private static HomeAssistantOptions Clone(HomeAssistantOptions o) => new()
    {
        Enabled = o.Enabled,
        BaseUrl = o.BaseUrl,
        Token = o.Token
    };

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _loop?.Wait(TimeSpan.FromSeconds(2));
            _seedLoop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // ignore
        }
        finally
        {
            _http?.Dispose();
            _http = null;
            HomeAssistantBridge.Connected = false;
            HomeAssistantBridge.Clear();
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var wsUrl = BuildWebSocketUrl(_options.BaseUrl);
        var backoffSeconds = 2;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(wsUrl), ct);

                if (!await AuthenticateAsync(ws, ct))
                {
                    Console.WriteLine("[HA] Authentication failed — check the token. Retrying in 30s.");
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    continue;
                }

                await SendAsync(ws, new { id = 1, type = "get_states" }, ct);
                await SendAsync(ws, new { id = 2, type = "subscribe_events", event_type = "state_changed" }, ct);

                Console.WriteLine("[HA] Connected and subscribed to state changes");
                HomeAssistantBridge.Connected = true;
                backoffSeconds = 2; // reset after a good connection

                await ReceiveLoopAsync(ws, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HA] Connection error: {ex.Message}");
            }

            HomeAssistantBridge.Connected = false;
            if (ct.IsCancellationRequested) break;

            await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), ct).ContinueWith(_ => { }, ct);
            backoffSeconds = Math.Min(backoffSeconds * 2, 30); // exponential backoff up to 30s
        }
    }

    /// <summary>
    ///     Periodically seeds the rolling history buffer for watched entities from the HA History REST API, so
    ///     graphs have real depth immediately and after a restart. Live samples are appended via StoreEntity.
    /// </summary>
    private async Task SeedLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (HomeAssistantBridge.Connected && _http != null)
                    foreach (var id in HomeAssistantBridge.GetUnseededWatched())
                    {
                        if (ct.IsCancellationRequested) break;
                        await SeedEntityAsync(id, ct);
                    }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HA] history seed loop error: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(15), ct).ContinueWith(_ => { }, ct);
        }
    }

    private async Task SeedEntityAsync(string entityId, CancellationToken ct)
    {
        try
        {
            var start = DateTime.UtcNow.AddHours(-HistoryHours).ToString("o", CultureInfo.InvariantCulture);
            var url = $"api/history/period/{Uri.EscapeDataString(start)}" +
                      $"?filter_entity_id={Uri.EscapeDataString(entityId)}&no_attributes";
            var json = await _http!.GetStringAsync(url, ct);
            var samples = ParseHistory(json);
            if (samples.Count > 0) HomeAssistantBridge.SeedHistory(entityId, samples);
            HomeAssistantBridge.MarkSeeded(entityId);
            Console.WriteLine($"[HA] Seeded {samples.Count} history points for {entityId}");
        }
        catch (Exception ex)
        {
            // Mark seeded anyway so we don't hammer HA; live samples still accumulate.
            HomeAssistantBridge.MarkSeeded(entityId);
            Console.WriteLine($"[HA] history fetch failed for {entityId}: {ex.Message}");
        }
    }

    private static List<HaSample> ParseHistory(string json)
    {
        var result = new List<HaSample>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return result;

        foreach (var st in root[0].EnumerateArray())
        {
            if (!st.TryGetProperty("state", out var sv)) continue;
            if (!double.TryParse(sv.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
                continue;

            var ts = DateTime.UtcNow;
            if (st.TryGetProperty("last_changed", out var lc) && lc.ValueKind == JsonValueKind.String)
            {
                if (lc.TryGetDateTime(out var dt)) ts = dt.ToUniversalTime();
                else if (DateTimeOffset.TryParse(lc.GetString(), CultureInfo.InvariantCulture,
                             DateTimeStyles.AssumeUniversal, out var dto)) ts = dto.UtcDateTime;
            }

            result.Add(new HaSample(ts, num));
        }

        return result;
    }

    private async Task<bool> AuthenticateAsync(ClientWebSocket ws, CancellationToken ct)
    {
        // HA sends auth_required first.
        var hello = await ReceiveMessageAsync(ws, ct);
        if (hello == null) return false;

        await SendAsync(ws, new { type = "auth", access_token = _options.Token }, ct);

        var result = await ReceiveMessageAsync(ws, ct);
        if (result == null) return false;

        using var doc = JsonDocument.Parse(result);
        return doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "auth_ok";
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var message = await ReceiveMessageAsync(ws, ct);
            if (message == null) break;
            HandleMessage(message);
        }
    }

    private static void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            switch (type)
            {
                case "result":
                    // Initial snapshot from get_states: { result: [ {entity_id, state, attributes}, ... ] }
                    if (root.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.Array)
                        foreach (var entity in res.EnumerateArray())
                            StoreEntity(entity);
                    break;

                case "event":
                    // state_changed: { event: { data: { entity_id, new_state: {...} } } }
                    if (root.TryGetProperty("event", out var ev) &&
                        ev.TryGetProperty("data", out var data) &&
                        data.TryGetProperty("new_state", out var newState) &&
                        newState.ValueKind == JsonValueKind.Object)
                        StoreEntity(newState);
                    break;
            }
        }
        catch
        {
            // Ignore malformed frames
        }
    }

    private static void StoreEntity(JsonElement entity)
    {
        if (!entity.TryGetProperty("entity_id", out var idEl)) return;
        var id = idEl.GetString();
        if (string.IsNullOrEmpty(id)) return;

        var state = entity.TryGetProperty("state", out var s) ? s.GetString() ?? "" : "";
        string? unit = null;
        string? friendly = null;
        string? icon = null;
        string? deviceClass = null;
        if (entity.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
        {
            if (attrs.TryGetProperty("unit_of_measurement", out var u)) unit = u.GetString();
            if (attrs.TryGetProperty("friendly_name", out var f)) friendly = f.GetString();
            if (attrs.TryGetProperty("icon", out var ic)) icon = ic.GetString();
            // HA only sends "icon" for entities with a CUSTOM icon; default icons are derived from
            // device_class, so capture it to pick a sensible icon when "icon" is absent.
            if (attrs.TryGetProperty("device_class", out var dc)) deviceClass = dc.GetString();
        }

        DateTime? lastChanged = null;
        if (entity.TryGetProperty("last_changed", out var lc) && lc.ValueKind == JsonValueKind.String)
        {
            if (lc.TryGetDateTime(out var lcDt))
                lastChanged = lcDt.ToUniversalTime();
            else if (DateTimeOffset.TryParse(lc.GetString(), CultureInfo.InvariantCulture,
                         DateTimeStyles.AssumeUniversal, out var dto))
                lastChanged = dto.UtcDateTime;
        }

        HomeAssistantBridge.Set(id, state, unit, friendly, icon, deviceClass, lastChanged);

        // Buffer numeric samples for entities a graph/sparkline is watching.
        if (double.TryParse(state, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
            HomeAssistantBridge.AddSample(id, num, lastChanged ?? DateTime.UtcNow);
    }

    private static async Task SendAsync(ClientWebSocket ws, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static async Task<string?> ReceiveMessageAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string BuildWebSocketUrl(string baseUrl)
    {
        var trimmed = (baseUrl ?? "").TrimEnd('/');
        if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return "wss://" + trimmed["https://".Length..] + "/api/websocket";
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return "ws://" + trimmed["http://".Length..] + "/api/websocket";
        return "ws://" + trimmed + "/api/websocket"; // assume plain http if no scheme
    }
}
