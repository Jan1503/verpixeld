using System.Text.Json;
using System.Text.Json.Serialization;
using CanvasManagement;
using CanvasManagement.Interfaces;
using verpixeld.Configuration;
using verpixeld.Interfaces;
using verpixeld.Layout;

namespace verpixeld.Services;

/// <summary>
///     Registers the LED wall as an MQTT device in Home Assistant (notify, toast text, last-toast
///     sensor, layout select, night-mode switch + night-active, brightness). Commands arrive on
///     <c>verpixeld/wall/#</c> via the existing HA websocket.
/// </summary>
public sealed class HaWallDevice : IDisposable
{
    public const string TopicPrefix = "verpixeld/wall";

    private static readonly JsonSerializerOptions MqttJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HomeAssistantService _ha;
    private readonly CanvasManager _canvas;
    private readonly INightModeManager _night;
    private readonly LayoutStorageManager _layouts;
    private readonly ILayoutLoaderService _loader;
    private readonly Func<string?> _configUrl;
    private readonly object _lock = new();
    private System.Threading.Timer? _timer;
    private bool _disposed;
    private bool _published;
    private string[] _lastOptions = [];
    private string _lastToastState = "—";
    private string _lastToastAttrs = "{}";

    public HaWallDevice(
        HomeAssistantService ha,
        CanvasManager canvas,
        INightModeManager night,
        LayoutStorageManager layouts,
        ILayoutLoaderService loader,
        Func<string?> configUrl)
    {
        _ha = ha;
        _canvas = canvas;
        _night = night;
        _layouts = layouts;
        _loader = loader;
        _configUrl = configUrl;
        _ha.MqttMessage += OnMqtt;
        _ha.DeviceChannelReady += OnChannelReady;
        HomeAssistantBridge.Notification += OnToast;
        _timer = new System.Threading.Timer(_ =>
        {
            try { _ = PublishStateAsync(); }
            catch (Exception ex) { Console.WriteLine($"[HA Device] state: {ex.Message}"); }
        }, null, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15));
        Console.WriteLine("[HA Device] Wall device ready (MQTT discovery when HA MQTT is available)");
    }

    public object Status()
    {
        var expose = _ha.Snapshot().ExposeDevice;
        return new
        {
            expose,
            mqtt = _ha.MqttAvailable,
            published = _published,
            layout = _loader.CurrentLayoutName,
            nightMode = _night.GetConfiguration().Enabled,
            nightActive = _night.GetStatus().isActive,
            brightnessPercent = (int)Math.Round(Math.Clamp(_canvas.Brightness, 0, 1) * 100),
            hint = !_ha.MqttAvailable
                ? "Enable the MQTT integration (Mosquitto addon) so the wall appears as a device."
                : expose ? "Device published. Look for notify.verpixeld_wall, text.verpixeld_toast, binary_sensor.verpixeld_night_active, switch.verpixeld_night_mode, select.verpixeld_layout, number.verpixeld_brightness."
                    : "Device exposure is off."
        };
    }

    public async Task RefreshAsync()
    {
        if (_disposed) return;
        if (_ha.MqttAvailable && _ha.Snapshot().ExposeDevice)
            await PublishDiscoveryAsync();
        else if (_published)
            await UnpublishAsync();
        await PublishStateAsync();
    }

    private void OnChannelReady()
    {
        _ = RefreshAsync();
    }

    private void OnMqtt(string topic, string payload)
    {
        if (_disposed) return;
        if (!topic.StartsWith(TopicPrefix, StringComparison.OrdinalIgnoreCase)) return;
        var tail = topic[TopicPrefix.Length..].Trim('/');
        if (IsCommandTopic(tail))
            Console.WriteLine($"[HA Device] {tail}: {TrimForLog(payload)}");
        _ = HandleCommandAsync(tail, payload);
    }

    private async Task HandleCommandAsync(string tail, string payload)
    {
        try
        {
            if (tail.Equals("notify", StringComparison.OrdinalIgnoreCase)
                || tail.Equals("toast/set", StringComparison.OrdinalIgnoreCase))
            {
                ApplyNotify(payload);
                var echo = (payload ?? "").Trim();
                if (echo.Length > 255) echo = echo[..252] + "...";
                await _ha.PublishMqttAsync($"{TopicPrefix}/toast/state", echo, retain: false);
                return;
            }

            if (tail.Equals("night_mode/set", StringComparison.OrdinalIgnoreCase))
            {
                var on = payload.Equals("ON", StringComparison.OrdinalIgnoreCase)
                         || payload.Equals("true", StringComparison.OrdinalIgnoreCase)
                         || payload == "1";
                SetNightMode(on);
                await PublishStateAsync();
                return;
            }

            if (tail.Equals("layout/set", StringComparison.OrdinalIgnoreCase))
            {
                await LoadLayoutAsync(payload.Trim());
                await PublishStateAsync();
                return;
            }

            if (tail.Equals("brightness/set", StringComparison.OrdinalIgnoreCase))
            {
                if (double.TryParse(payload.Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var pct))
                {
                    _canvas.Brightness = (float)Math.Clamp(pct / 100.0, 0, 1);
                    Console.WriteLine($"[HA Device] brightness {(int)Math.Round(pct)}%");
                }
                await PublishStateAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HA Device] command {tail}: {ex.Message}");
        }
    }

    internal static void ApplyNotify(string payload)
    {
        var title = "Home Assistant";
        var message = payload ?? "";
        string? severity = null;
        string? id = null;

        var trimmed = message.Trim();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                title = Read(root, "title") ?? title;
                message = Read(root, "message") ?? Read(root, "text") ?? message;
                severity = Read(root, "severity");
                id = Read(root, "notification_id") ?? Read(root, "notificationId");
                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                {
                    severity ??= Read(data, "severity");
                    id ??= Read(data, "notification_id") ?? Read(data, "notificationId");
                }
            }
            catch { /* plain text that happened to look like JSON */ }
        }
        else
        {
            var nl = trimmed.IndexOf('\n');
            var pipe = trimmed.IndexOf(" | ", StringComparison.Ordinal);
            if (nl > 0)
            {
                title = trimmed[..nl].Trim();
                message = trimmed[(nl + 1)..].Trim();
            }
            else if (pipe > 0)
            {
                title = trimmed[..pipe].Trim();
                message = trimmed[(pipe + 3)..].Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(message)) return;
        HomeAssistantBridge.RaiseNotification(title, message, id, severity);
    }

    private static string? Read(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private void SetNightMode(bool enabled)
    {
        var cfg = _night.GetConfiguration();
        var next = new NightModeConfiguration
        {
            Enabled = enabled,
            StartTime = cfg.StartTime,
            EndTime = cfg.EndTime,
            NightBrightness = cfg.NightBrightness,
            DayBrightness = cfg.DayBrightness,
            TransitionMinutes = cfg.TransitionMinutes,
            ActiveDays = [.. cfg.ActiveDays]
        };
        _night.UpdateConfiguration(next);
        Console.WriteLine($"[HA Device] night mode {(enabled ? "on" : "off")}");
    }

    private async Task LoadLayoutAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "(none)" or "-") return;
        var layout = _layouts.LoadLayout(name);
        if (layout == null)
        {
            Console.WriteLine($"[HA Device] layout '{name}' not found");
            return;
        }

        var result = await _loader.LoadLayoutAsync(layout, "HA");
        Console.WriteLine(result.Success
            ? $"[HA Device] loaded layout '{name}'"
            : $"[HA Device] layout '{name}' failed: {result.ErrorMessage}");
    }

    private async Task PublishDiscoveryAsync()
    {
        var device = DevicePayload();
        var avail = $"{TopicPrefix}/availability";
        var options = LayoutOptions();
        lock (_lock) _lastOptions = options;

        await PubConfig("notify", "verpixeld_wall", new
        {
            Name = "Wall",
            UniqueId = "verpixeld_wall_notify",
            CommandTopic = $"{TopicPrefix}/notify",
            AvailabilityTopic = avail,
            Icon = "mdi:wall",
            Device = device
        });

        await PubConfig("text", "verpixeld_toast", new
        {
            Name = "Toast",
            UniqueId = "verpixeld_wall_toast",
            CommandTopic = $"{TopicPrefix}/toast/set",
            StateTopic = $"{TopicPrefix}/toast/state",
            Mode = "text",
            Max = 255,
            AvailabilityTopic = avail,
            Icon = "mdi:message-plus",
            Device = device
        });

        await PubConfig("sensor", "verpixeld_last_toast", new
        {
            Name = "Last toast",
            UniqueId = "verpixeld_wall_last_toast",
            StateTopic = $"{TopicPrefix}/last_toast/state",
            JsonAttributesTopic = $"{TopicPrefix}/last_toast/attrs",
            AvailabilityTopic = avail,
            Icon = "mdi:message-text",
            Device = device
        });

        await PubConfig("switch", "verpixeld_night_mode", new
        {
            Name = "Night mode schedule",
            UniqueId = "verpixeld_wall_night_mode",
            CommandTopic = $"{TopicPrefix}/night_mode/set",
            StateTopic = $"{TopicPrefix}/night_mode/state",
            PayloadOn = "ON",
            PayloadOff = "OFF",
            AvailabilityTopic = avail,
            Icon = "mdi:calendar-clock",
            Device = device
        });

        await PubConfig("binary_sensor", "verpixeld_night_active", new
        {
            Name = "Night active",
            UniqueId = "verpixeld_wall_night_active",
            StateTopic = $"{TopicPrefix}/night_active/state",
            PayloadOn = "ON",
            PayloadOff = "OFF",
            AvailabilityTopic = avail,
            Icon = "mdi:weather-night",
            Device = device
        });

        await PubConfig("select", "verpixeld_layout", new
        {
            Name = "Layout",
            UniqueId = "verpixeld_wall_layout",
            CommandTopic = $"{TopicPrefix}/layout/set",
            StateTopic = $"{TopicPrefix}/layout/state",
            Options = options,
            AvailabilityTopic = avail,
            Icon = "mdi:view-dashboard",
            Device = device
        });

        await PubConfig("number", "verpixeld_brightness", new
        {
            Name = "Brightness",
            UniqueId = "verpixeld_wall_brightness",
            CommandTopic = $"{TopicPrefix}/brightness/set",
            StateTopic = $"{TopicPrefix}/brightness/state",
            Min = 0,
            Max = 100,
            Step = 1,
            Mode = "slider",
            UnitOfMeasurement = "%",
            AvailabilityTopic = avail,
            Icon = "mdi:brightness-6",
            Device = device
        });

        await _ha.PublishMqttAsync(avail, "online", retain: true);
        await _ha.PublishMqttAsync($"{TopicPrefix}/toast/state", "", retain: false);
        await _ha.PublishMqttAsync($"{TopicPrefix}/last_toast/state", _lastToastState, retain: true);
        await _ha.PublishMqttAsync($"{TopicPrefix}/last_toast/attrs", _lastToastAttrs, retain: true);
        _published = true;
        Console.WriteLine("[HA Device] discovery published");
        await PublishStateAsync();
    }

    private async Task UnpublishAsync()
    {
        foreach (var (component, id) in new[]
                 {
                     ("notify", "verpixeld_wall"),
                     ("text", "verpixeld_toast"),
                     ("sensor", "verpixeld_last_toast"),
                     ("switch", "verpixeld_night_mode"),
                     ("binary_sensor", "verpixeld_night_active"),
                     ("select", "verpixeld_layout"),
                     ("number", "verpixeld_brightness")
                 })
            await _ha.PublishMqttAsync($"homeassistant/{component}/{id}/config", "", retain: true);
        await _ha.PublishMqttAsync($"{TopicPrefix}/availability", "offline", retain: true);
        _published = false;
        Console.WriteLine("[HA Device] discovery removed");
    }

    private async Task PublishStateAsync()
    {
        if (_disposed || !_ha.MqttAvailable || !_ha.Snapshot().ExposeDevice) return;
        var options = LayoutOptions();
        bool optionsChanged;
        lock (_lock)
        {
            optionsChanged = !_lastOptions.SequenceEqual(options);
            if (optionsChanged) _lastOptions = options;
        }

        if (optionsChanged && _published)
            await PublishDiscoveryAsync();

        var layout = _loader.CurrentLayoutName;
        if (string.IsNullOrWhiteSpace(layout) || !options.Contains(layout, StringComparer.OrdinalIgnoreCase))
            layout = options.Length > 0 ? options[0] : "(none)";
        var night = _night.GetConfiguration().Enabled ? "ON" : "OFF";
        var nightActive = _night.GetStatus().isActive ? "ON" : "OFF";
        var brightness = ((int)Math.Round(Math.Clamp(_canvas.Brightness, 0, 1) * 100)).ToString(
            System.Globalization.CultureInfo.InvariantCulture);

        await _ha.PublishMqttAsync($"{TopicPrefix}/availability", "online", retain: true);
        await _ha.PublishMqttAsync($"{TopicPrefix}/night_mode/state", night, retain: true);
        await _ha.PublishMqttAsync($"{TopicPrefix}/night_active/state", nightActive, retain: true);
        await _ha.PublishMqttAsync($"{TopicPrefix}/layout/state", layout, retain: true);
        await _ha.PublishMqttAsync($"{TopicPrefix}/brightness/state", brightness, retain: true);
    }

    private async Task PubConfig(string component, string objectId, object payload)
    {
        var json = JsonSerializer.Serialize(payload, MqttJson);
        await _ha.PublishMqttAsync($"homeassistant/{component}/{objectId}/config", json, retain: true);
    }

    private object DevicePayload() => new
    {
        Identifiers = new[] { "verpixeld_wall" },
        Name = "verpixeld",
        Manufacturer = "verpixeld",
        Model = "LED Wall",
        ConfigurationUrl = _configUrl()
    };

    private string[] LayoutOptions()
    {
        var names = _layouts.GetAllLayouts()
            .Select(l => l.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return names.Length > 0 ? names : ["(none)"];
    }

    private void OnToast(HaNotification n)
    {
        if (_disposed) return;
        _ = PublishLastToastAsync(n);
    }

    private async Task PublishLastToastAsync(HaNotification n)
    {
        if (!_ha.MqttAvailable || !_ha.Snapshot().ExposeDevice) return;
        var msg = (n.Message ?? "").Trim();
        if (string.IsNullOrWhiteSpace(msg)) msg = (n.Title ?? "").Trim();
        if (msg.Length > 255) msg = msg[..252] + "...";
        if (string.IsNullOrWhiteSpace(msg)) msg = "—";
        var attrs = JsonSerializer.Serialize(new
        {
            title = n.Title,
            message = n.Message,
            severity = n.Severity,
            notification_id = n.NotificationId,
            at = DateTime.Now.ToString("o")
        });
        _lastToastState = msg;
        _lastToastAttrs = attrs;
        await _ha.PublishMqttAsync($"{TopicPrefix}/last_toast/state", msg, retain: true);
        await _ha.PublishMqttAsync($"{TopicPrefix}/last_toast/attrs", attrs, retain: true);
    }

    private static bool IsCommandTopic(string tail) =>
        tail.Equals("notify", StringComparison.OrdinalIgnoreCase)
        || tail.EndsWith("/set", StringComparison.OrdinalIgnoreCase);

    private static string TrimForLog(string s) =>
        s.Length <= 120 ? s : s[..117] + "...";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        HomeAssistantBridge.Notification -= OnToast;
        _ha.MqttMessage -= OnMqtt;
        _ha.DeviceChannelReady -= OnChannelReady;
        _timer?.Dispose();
        _timer = null;
        try
        {
            if (_published)
                _ha.PublishMqttAsync($"{TopicPrefix}/availability", "offline", retain: true)
                    .GetAwaiter().GetResult();
        }
        catch { }
    }
}
