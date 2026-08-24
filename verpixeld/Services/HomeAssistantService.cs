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
    private const int IdGetStates = 1;
    private const int IdSubEvents = 2;
    private const int IdSubPersistent = 3;
    private const int IdSubMqtt = 4;

    private readonly object _optionsLock = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private HomeAssistantOptions _options;
    private CancellationTokenSource? _cts;
    private HttpClient? _http;
    private Task? _loop;
    private Task? _seedLoop;
    private ClientWebSocket? _ws;
    private int _nextId = 20;
    private volatile bool _mqttResultSeen;

    /// <summary>True when HA accepted <c>mqtt/subscribe</c> (MQTT integration is loaded).</summary>
    public bool MqttAvailable { get; private set; }

    /// <summary>Fired once per connection after the MQTT subscribe result (or a short timeout).</summary>
    public event Action? DeviceChannelReady;

    /// <summary>MQTT messages from <c>mqtt/subscribe</c> (topic, payload).</summary>
    public event Action<string, string>? MqttMessage;

    /// <summary>Optional wall-device publisher (set after construction to avoid a cycle).</summary>
    public HaWallDevice? WallDevice { get; set; }

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

    /// <summary>
    ///     Apply settings. Reconnects only when connection fields change; toast style is live.
    /// </summary>
    /// <returns>True when the WebSocket was restarted.</returns>
    public bool Apply(HomeAssistantOptions next)
    {
        bool reconnect;
        lock (_optionsLock)
        {
            var url = string.IsNullOrWhiteSpace(next.BaseUrl) ? _options.BaseUrl : next.BaseUrl.Trim();
            var token = next.Token ?? "";
            reconnect = next.Enabled != _options.Enabled
                        || !string.Equals(url, _options.BaseUrl, StringComparison.Ordinal)
                        || !string.Equals(token, _options.Token, StringComparison.Ordinal);
            _options.Enabled = next.Enabled;
            _options.BaseUrl = url;
            _options.Token = token;
            _options.Toast = (next.Toast ?? new HomeAssistantToastOptions()).Clone();
            _options.ExposeDevice = next.ExposeDevice;
        }

        if (reconnect)
        {
            Stop();
            Start();
        }
        else
        {
            _ = WallDevice?.RefreshAsync();
        }

        return reconnect;
    }

    private static HomeAssistantOptions Clone(HomeAssistantOptions o) => new()
    {
        Enabled = o.Enabled,
        BaseUrl = o.BaseUrl,
        Token = o.Token,
        Toast = (o.Toast ?? new HomeAssistantToastOptions()).Clone(),
        ExposeDevice = o.ExposeDevice
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
            _ws = null;
            MqttAvailable = false;
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

                _ws = ws;
                _mqttResultSeen = false;
                MqttAvailable = false;

                await SendAsync(ws, new { id = IdGetStates, type = "get_states" }, ct);
                await SendAsync(ws, new { id = IdSubEvents, type = "subscribe_events", event_type = "state_changed" }, ct);
                await SendAsync(ws, new { id = IdSubPersistent, type = "persistent_notification/subscribe" }, ct);
                await SendAsync(ws, new { id = IdSubMqtt, type = "mqtt/subscribe", topic = "verpixeld/wall/#" }, ct);
                _ = WatchMqttSubscribeAsync(ct);

                Console.WriteLine("[HA] Connected (states + persistent notifications + MQTT device)");
                HomeAssistantBridge.Connected = true;
                backoffSeconds = 2;

                await ReceiveLoopAsync(ws, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                var hint = wsUrl.Contains(".local", StringComparison.OrdinalIgnoreCase)
                    ? " Docker cannot resolve .local (mDNS). Use the Home Assistant LAN IP, e.g. http://192.168.10.20:8123"
                    : "";
                Console.WriteLine($"[HA] Connection error: {ex.Message}.{hint}");
            }

            HomeAssistantBridge.Connected = false;
            MqttAvailable = false;
            _ws = null;
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

    public async Task PublishMqttAsync(string topic, string payload, bool retain = false)
    {
        var ws = _ws;
        var ct = _cts?.Token ?? CancellationToken.None;
        if (ws is not { State: WebSocketState.Open } || ct.IsCancellationRequested) return;
        var id = Interlocked.Increment(ref _nextId);
        // mqtt/subscribe exists as a websocket command; mqtt/publish does not on many HA versions
        // (unknown_command). Publishing goes through the mqtt.publish service instead.
        await SendAsync(ws, new
        {
            id,
            type = "call_service",
            domain = "mqtt",
            service = "publish",
            service_data = new
            {
                topic,
                payload,
                qos = 0,
                retain
            }
        }, ct);
    }

    private async Task WatchMqttSubscribeAsync(CancellationToken ct)
    {
        try { await Task.Delay(2500, ct); }
        catch (OperationCanceledException) { return; }
        if (_mqttResultSeen) return;
        MqttAvailable = false;
        Console.WriteLine("[HA] MQTT subscribe timed out — enable the MQTT integration in Home Assistant to expose the wall as a device");
        try { DeviceChannelReady?.Invoke(); }
        catch (Exception ex) { Console.WriteLine($"[HA] DeviceChannelReady: {ex.Message}"); }
    }

    private void NoteMqttResult(bool ok)
    {
        if (_mqttResultSeen) return;
        _mqttResultSeen = true;
        MqttAvailable = ok;
        Console.WriteLine(ok
            ? "[HA] MQTT device channel ready"
            : "[HA] MQTT not available (add the Mosquitto addon / MQTT integration — REST notify still works)");
        try { DeviceChannelReady?.Invoke(); }
        catch (Exception ex) { Console.WriteLine($"[HA] DeviceChannelReady: {ex.Message}"); }
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

    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            switch (type)
            {
                case "result":
                    var cmdId = root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var n) ? n : -1;
                    var success = !root.TryGetProperty("success", out var okEl) || okEl.ValueKind != JsonValueKind.False;
                    if (!success)
                    {
                        var err = root.TryGetProperty("error", out var errEl) ? errEl.ToString() : "";
                        Console.WriteLine($"[HA] command {cmdId} failed: {err}");
                        if (cmdId == IdSubMqtt) NoteMqttResult(false);
                        break;
                    }
                    if (cmdId == IdSubMqtt)
                    {
                        NoteMqttResult(true);
                        break;
                    }
                    if (root.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.Array)
                        foreach (var entity in res.EnumerateArray())
                            StoreEntity(entity, raiseToast: false);
                    break;

                case "event":
                    if (!root.TryGetProperty("event", out var ev)) break;
                    if (ev.TryGetProperty("topic", out var topicEl) && topicEl.ValueKind == JsonValueKind.String)
                    {
                        var topic = topicEl.GetString() ?? "";
                        var payload = ev.TryGetProperty("payload", out var pEl)
                            ? pEl.ValueKind == JsonValueKind.String ? pEl.GetString() ?? "" : pEl.GetRawText()
                            : "";
                        try { MqttMessage?.Invoke(topic, payload); }
                        catch (Exception ex) { Console.WriteLine($"[HA] MQTT handler: {ex.Message}"); }
                        break;
                    }
                    if (ev.TryGetProperty("notifications", out _))
                    {
                        HandlePersistentNotifications(ev);
                        break;
                    }
                    if (ev.TryGetProperty("data", out var data))
                    {
                        if (data.TryGetProperty("notifications", out _))
                        {
                            HandlePersistentNotifications(data);
                            break;
                        }
                        if (data.TryGetProperty("new_state", out var newState) &&
                            newState.ValueKind == JsonValueKind.Object)
                            StoreEntity(newState, raiseToast: true);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HA] message parse error: {ex.Message}");
        }
    }

    private static void StoreEntity(JsonElement entity, bool raiseToast)
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
        StoreExtra(id, entity, state, raiseToast);

        // Buffer numeric samples for entities a graph/sparkline is watching.
        if (double.TryParse(state, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
            HomeAssistantBridge.AddSample(id, num, lastChanged ?? DateTime.UtcNow);
    }

    private static void StoreExtra(string id, JsonElement entity, string state, bool raiseToast)
    {
        var bag = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (entity.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
        {
            // Keep every scalar attribute (weather, media, climate, and date-keyed waste schedules
            // such as Stadtreinigung Hamburg: "24.08.2026" → "Gelbe Tonne").
            foreach (var prop in attrs.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                    bag[prop.Name] = prop.Value.GetString() ?? "";
                else if (prop.Value.ValueKind == JsonValueKind.Number)
                    bag[prop.Name] = prop.Value.GetRawText();
                else if (prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    bag[prop.Name] = prop.Value.GetRawText();
                else if (prop.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                    bag[prop.Name] = prop.Value.GetRawText();
            }
        }

        HomeAssistantBridge.SetExtra(id, bag);

        // Pre-2023.6 HA still exposed persistent_notification.* as states.
        if (raiseToast &&
            id.StartsWith("persistent_notification.", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(state, "off", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(state, "removed", StringComparison.OrdinalIgnoreCase))
        {
            var title = bag.GetValueOrDefault("title") ?? bag.GetValueOrDefault("friendly_name") ?? "Home Assistant";
            var msg = bag.GetValueOrDefault("message") ?? state;
            if (!string.IsNullOrWhiteSpace(msg) && msg is not "notifying" and not "unknown")
                HomeAssistantBridge.RaiseNotification(title, msg, id);
        }
    }

    /// <summary>
    ///     HA 2023.6+ websocket push from <c>persistent_notification/subscribe</c>.
    ///     <c>current</c> is the snapshot at subscribe time (do not toast). <c>added</c>/<c>updated</c> are live.
    /// </summary>
    private static void HandlePersistentNotifications(JsonElement ev)
    {
        var kind = ev.TryGetProperty("type", out var t) ? t.GetString()
            : ev.TryGetProperty("update_type", out var ut) ? ut.GetString()
            : null;
        if (!ev.TryGetProperty("notifications", out var notes)) return;

        Console.WriteLine($"[HA] persistent_notification event: {kind ?? "(no type)"}");

        if (kind is "current" or "removed")
        {
            var n = notes.ValueKind == JsonValueKind.Object ? notes.EnumerateObject().Count()
                : notes.ValueKind == JsonValueKind.Array ? notes.GetArrayLength() : 0;
            if (kind == "current")
                Console.WriteLine($"[HA] persistent notifications: {n} existing (not toasted)");
            return;
        }

        if (kind is not null and not "added" and not "updated") return;

        if (notes.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in notes.EnumerateObject())
                RaiseFromNotification(item.Value, item.Name);
        }
        else if (notes.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in notes.EnumerateArray())
                RaiseFromNotification(item);
        }
    }

    private static void RaiseFromNotification(JsonElement n, string? key = null)
    {
        var title = ReadAttr(n, "title") ?? "Home Assistant";
        var msg = ReadAttr(n, "message");
        if (string.IsNullOrWhiteSpace(msg)) return;
        var id = ReadAttr(n, "notification_id") ?? key;
        Console.WriteLine($"[HA] persistent notification ({id}): {title}: {msg}");
        HomeAssistantBridge.RaiseNotification(title, msg, id);
    }

    private static string? ReadAttr(JsonElement n, string name)
    {
        if (!n.TryGetProperty(name, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private async Task SendAsync(ClientWebSocket ws, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(ct);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
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
