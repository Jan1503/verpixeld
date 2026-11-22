using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using verpixeld.MediaPlayer;
using verpixeld.MediaPlayer.Audio;

namespace verpixeld.Services;

/// <summary>
///     The verpixeld-audio board as a second MQTT device in Home Assistant: volume, mute,
///     now playing, playback state, radio station, transport buttons, "remember song",
///     sleep timer, alarm, wall spectrum and whether the board is reachable.
///     Commands arrive on <c>verpixeld/audio/#</c> through the same HA connection as the wall.
/// </summary>
public sealed class HaAudioDevice : IDisposable
{
    public const string TopicPrefix = "verpixeld/audio";
    private const string Off = "(off)";

    private static readonly JsonSerializerOptions MqttJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HomeAssistantService _ha;
    private readonly PixToneOutputService _board;
    private readonly MediaPlayerService _media;
    private readonly RadioService _radio;
    private readonly SleepTimerService _sleep;
    private readonly AlarmService _alarms;
    private readonly SongLogService _songs;
    private readonly IAudioVisualizerService _visualizer;
    private readonly PanelBridgeService _bridge;
    private readonly Func<string?> _configUrl;
    private readonly object _lock = new();
    private System.Threading.Timer? _timer;
    private bool _published;
    private bool _disposed;
    private string[] _stationOptions = [];
    private string _lastState = "";
    private int _publishing;

    private static readonly (string Component, string Id)[] Entities =
    {
        ("number", "verpixeld_audio_volume"),
        ("switch", "verpixeld_audio_mute"),
        ("sensor", "verpixeld_audio_title"),
        ("sensor", "verpixeld_audio_state"),
        ("select", "verpixeld_audio_station"),
        ("button", "verpixeld_audio_play_pause"),
        ("button", "verpixeld_audio_stop"),
        ("button", "verpixeld_audio_next"),
        ("button", "verpixeld_audio_previous"),
        ("button", "verpixeld_audio_save_song"),
        ("number", "verpixeld_audio_sleep"),
        ("switch", "verpixeld_audio_alarm"),
        ("sensor", "verpixeld_audio_next_alarm"),
        ("switch", "verpixeld_audio_wall_spectrum"),
        ("binary_sensor", "verpixeld_audio_online")
    };

    public HaAudioDevice(HomeAssistantService ha, PixToneOutputService board, MediaPlayerService media,
        RadioService radio, SleepTimerService sleep, AlarmService alarms, SongLogService songs,
        IAudioVisualizerService visualizer, PanelBridgeService bridge, Func<string?> configUrl)
    {
        _ha = ha;
        _board = board;
        _media = media;
        _radio = radio;
        _sleep = sleep;
        _alarms = alarms;
        _songs = songs;
        _visualizer = visualizer;
        _bridge = bridge;
        _configUrl = configUrl;
        _ha.MqttMessage += OnMqtt;
        _ha.DeviceChannelReady += OnChannelReady;
        _radio.NowChanged += Poke;
        _radio.StationsChanged += Poke;
        _sleep.Changed += Poke;
        _alarms.Changed += Poke;
        _timer = new System.Threading.Timer(_ => Poke(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
        Console.WriteLine("[HA Audio] Audio board device ready (MQTT discovery when HA MQTT is available)");
    }

    private bool Exposed => _ha.MqttAvailable && _ha.Snapshot().ExposeDevice && _board.IsSelected;

    private void Poke()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _publishing, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (Exposed && !_published) await PublishDiscoveryAsync();
                else if (!Exposed && _published && _ha.MqttAvailable) await UnpublishAsync();
                await PublishStateAsync(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[HA Audio] " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _publishing, 0);
            }
        });
    }

    private void OnChannelReady()
    {
        _published = false;
        Poke();
    }

    private void OnMqtt(string topic, string payload)
    {
        if (_disposed || !topic.StartsWith(TopicPrefix + "/", StringComparison.OrdinalIgnoreCase)) return;
        var tail = topic[(TopicPrefix.Length + 1)..];
        if (!tail.EndsWith("/set", StringComparison.OrdinalIgnoreCase) &&
            !tail.EndsWith("/press", StringComparison.OrdinalIgnoreCase)) return;
        Console.WriteLine($"[HA Audio] {tail}: {(payload.Length > 80 ? payload[..80] : payload)}");
        _ = Task.Run(async () =>
        {
            try
            {
                await HandleAsync(tail.ToLowerInvariant(), payload.Trim());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HA Audio] {tail}: {ex.Message}");
            }
            await PublishStateAsync(true);
        });
    }

    private static bool IsOn(string p) =>
        p.Equals("ON", StringComparison.OrdinalIgnoreCase) || p == "1" || p.Equals("true", StringComparison.OrdinalIgnoreCase);

    private async Task HandleAsync(string tail, string payload)
    {
        switch (tail)
        {
            case "volume/set":
                if (double.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    _board.SetVolume((int)Math.Round(Math.Clamp(v, 0, 100)), false);
                break;
            case "mute/set":
                _board.SetVolume(_board.Volume, IsOn(payload));
                break;
            case "station/set":
                if (payload == Off)
                {
                    if (_media.IsRadioPlaying) await _radio.StopAsync();
                    break;
                }
                var st = _radio.Stations.FirstOrDefault(s => s.Name == payload);
                if (st != null) await _radio.PlayAsync(st);
                break;
            case "play_pause/press":
                if (_media.IsRunning) _media.TogglePause();
                else if (_radio.LastStation is { } last) await _radio.PlayAsync(last);
                break;
            case "stop/press":
                if (_alarms.Ringing != null) _alarms.StopRinging();
                if (_media.IsRadioPlaying) await _radio.StopAsync();
                else await _media.StopAsync();
                break;
            case "next/press":
                if (_media.IsRadioPlaying) await _radio.StepAsync(1);
                else await _media.PlayNextAsync();
                break;
            case "previous/press":
                if (_media.IsRadioPlaying) await _radio.StepAsync(-1);
                else await _media.PlayPreviousAsync();
                break;
            case "save_song/press":
                _songs.SaveCurrent();
                break;
            case "sleep/set":
                if (double.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out var m))
                    _sleep.Set((int)Math.Round(Math.Clamp(m, 0, 240)));
                break;
            case "alarm/set":
            {
                var a = _alarms.PanelAlarm;
                if (a == null || a.Enabled != IsOn(payload)) _alarms.TogglePanelAlarm();
                break;
            }
            case "wall_spectrum/set":
            {
                var want = IsOn(payload);
                var running = _visualizer.IsRunning && _visualizer.UsingExternal;
                if (want != running) _bridge.ToggleVisualizerFromOutside();
                break;
            }
        }
    }

    private async Task PublishDiscoveryAsync()
    {
        var device = new
        {
            Identifiers = new[] { "verpixeld_audio" },
            Name = "verpixeld audio",
            Manufacturer = "verpixeld",
            Model = "Audio board",
            ConfigurationUrl = _configUrl(),
            ViaDevice = "verpixeld_wall"
        };
        var avail = $"{TopicPrefix}/availability";
        var options = StationOptions();
        lock (_lock) _stationOptions = options;

        async Task Pub(string component, string id, object cfg) =>
            await _ha.PublishMqttAsync($"homeassistant/{component}/{id}/config", JsonSerializer.Serialize(cfg, MqttJson), retain: true);

        await Pub("number", "verpixeld_audio_volume", new
        {
            Name = "Volume", UniqueId = "verpixeld_audio_volume", CommandTopic = $"{TopicPrefix}/volume/set",
            StateTopic = $"{TopicPrefix}/volume/state", Min = 0, Max = 100, Step = 1, Mode = "slider",
            UnitOfMeasurement = "%", AvailabilityTopic = avail, Icon = "mdi:volume-high", Device = device
        });
        await Pub("switch", "verpixeld_audio_mute", new
        {
            Name = "Mute", UniqueId = "verpixeld_audio_mute", CommandTopic = $"{TopicPrefix}/mute/set",
            StateTopic = $"{TopicPrefix}/mute/state", AvailabilityTopic = avail, Icon = "mdi:volume-off", Device = device
        });
        await Pub("sensor", "verpixeld_audio_title", new
        {
            Name = "Now playing", UniqueId = "verpixeld_audio_title", StateTopic = $"{TopicPrefix}/title/state",
            JsonAttributesTopic = $"{TopicPrefix}/title/attrs", AvailabilityTopic = avail, Icon = "mdi:music-note",
            Device = device
        });
        await Pub("sensor", "verpixeld_audio_state", new
        {
            Name = "Playback", UniqueId = "verpixeld_audio_state", StateTopic = $"{TopicPrefix}/state/state",
            DeviceClass = "enum", Options = new[] { "playing", "paused", "stopped" },
            AvailabilityTopic = avail, Icon = "mdi:play-pause", Device = device
        });
        await Pub("select", "verpixeld_audio_station", new
        {
            Name = "Radio station", UniqueId = "verpixeld_audio_station", CommandTopic = $"{TopicPrefix}/station/set",
            StateTopic = $"{TopicPrefix}/station/state", Options = options, AvailabilityTopic = avail,
            Icon = "mdi:radio", Device = device
        });
        foreach (var (key, name, icon) in new[]
                 {
                     ("play_pause", "Play / pause", "mdi:play-pause"),
                     ("stop", "Stop", "mdi:stop"),
                     ("next", "Next", "mdi:skip-next"),
                     ("previous", "Previous", "mdi:skip-previous"),
                     ("save_song", "Remember song", "mdi:heart-plus")
                 })
            await Pub("button", $"verpixeld_audio_{key}", new
            {
                Name = name, UniqueId = $"verpixeld_audio_{key}", CommandTopic = $"{TopicPrefix}/{key}/press",
                AvailabilityTopic = avail, Icon = icon, Device = device
            });
        await Pub("number", "verpixeld_audio_sleep", new
        {
            Name = "Sleep timer", UniqueId = "verpixeld_audio_sleep", CommandTopic = $"{TopicPrefix}/sleep/set",
            StateTopic = $"{TopicPrefix}/sleep/state", Min = 0, Max = 120, Step = 5, Mode = "box",
            UnitOfMeasurement = "min", AvailabilityTopic = avail, Icon = "mdi:sleep", Device = device
        });
        await Pub("switch", "verpixeld_audio_alarm", new
        {
            Name = "Alarm", UniqueId = "verpixeld_audio_alarm", CommandTopic = $"{TopicPrefix}/alarm/set",
            StateTopic = $"{TopicPrefix}/alarm/state", AvailabilityTopic = avail, Icon = "mdi:alarm", Device = device
        });
        await Pub("sensor", "verpixeld_audio_next_alarm", new
        {
            Name = "Next alarm", UniqueId = "verpixeld_audio_next_alarm", StateTopic = $"{TopicPrefix}/next_alarm/state",
            DeviceClass = "timestamp", AvailabilityTopic = avail, Icon = "mdi:alarm-check", Device = device
        });
        await Pub("switch", "verpixeld_audio_wall_spectrum", new
        {
            Name = "Wall spectrum", UniqueId = "verpixeld_audio_wall_spectrum",
            CommandTopic = $"{TopicPrefix}/wall_spectrum/set", StateTopic = $"{TopicPrefix}/wall_spectrum/state",
            AvailabilityTopic = avail, Icon = "mdi:equalizer", Device = device
        });
        await Pub("binary_sensor", "verpixeld_audio_online", new
        {
            Name = "Board online", UniqueId = "verpixeld_audio_online", StateTopic = $"{TopicPrefix}/online/state",
            DeviceClass = "connectivity", Device = device
        });
        _published = true;
        _lastState = "";
        Console.WriteLine("[HA Audio] discovery published");
    }

    private async Task UnpublishAsync()
    {
        foreach (var (component, id) in Entities)
            await _ha.PublishMqttAsync($"homeassistant/{component}/{id}/config", "", retain: true);
        await _ha.PublishMqttAsync($"{TopicPrefix}/availability", "offline", retain: true);
        _published = false;
        Console.WriteLine("[HA Audio] discovery removed");
    }

    private async Task PublishStateAsync(bool force)
    {
        if (_disposed || !_published || !Exposed) return;
        var options = StationOptions();
        bool changed;
        lock (_lock)
        {
            changed = !_stationOptions.SequenceEqual(options);
            if (changed) _stationOptions = options;
        }
        if (changed) await PublishDiscoveryAsync();

        var now = _radio.Now;
        var song = _songs.Current();
        var title = song?.Title ?? "";
        if (title.Length > 250) title = title[..250];
        var state = !_media.IsRunning ? "stopped" : _media.IsPaused ? "paused" : "playing";
        var station = now.Playing && now.Station != null && options.Contains(now.Station.Name) ? now.Station.Name : Off;
        var alarm = _alarms.PanelAlarm;
        var next = _alarms.NextAny();
        string nextIso = "None";
        if (next != null)
        {
            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
                nextIso = new DateTimeOffset(next.Value, zone.GetUtcOffset(next.Value)).ToString("o");
            }
            catch
            {
                nextIso = next.Value.ToString("o");
            }
        }
        var online = _board.BoardOnline;
        var spectrum = _visualizer.IsRunning && _visualizer.UsingExternal;
        var attrs = JsonSerializer.Serialize(new
        {
            station = now.Station?.Name,
            source = song?.Source,
            radio = now.Playing
        });

        var values = new (string Topic, string Value)[]
        {
            ("availability", online ? "online" : "offline"),
            ("online/state", online ? "ON" : "OFF"),
            ("volume/state", _board.Volume.ToString(CultureInfo.InvariantCulture)),
            ("mute/state", _board.Muted ? "ON" : "OFF"),
            ("title/state", string.IsNullOrWhiteSpace(title) ? "—" : title),
            ("title/attrs", attrs),
            ("state/state", state),
            ("station/state", station),
            ("sleep/state", _sleep.MinutesLeft.ToString(CultureInfo.InvariantCulture)),
            ("alarm/state", alarm is { Enabled: true } ? "ON" : "OFF"),
            ("next_alarm/state", nextIso),
            ("wall_spectrum/state", spectrum ? "ON" : "OFF")
        };
        var sig = string.Join("\u0001", values.Select(v => v.Value));
        if (!force && sig == _lastState) return;
        _lastState = sig;
        foreach (var (topic, value) in values)
            await _ha.PublishMqttAsync($"{TopicPrefix}/{topic}", value, retain: true);
    }

    private string[] StationOptions()
    {
        var names = _radio.Stations.Select(s => s.Name).Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct().Take(100).ToList();
        names.Insert(0, Off);
        return names.ToArray();
    }

    public object Status() => new
    {
        published = _published,
        mqtt = _ha.MqttAvailable,
        boardSelected = _board.IsSelected,
        boardOnline = _board.BoardOnline
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ha.MqttMessage -= OnMqtt;
        _ha.DeviceChannelReady -= OnChannelReady;
        _timer?.Dispose();
        _timer = null;
        try
        {
            if (_published)
                _ha.PublishMqttAsync($"{TopicPrefix}/availability", "offline", retain: true).GetAwaiter().GetResult();
        }
        catch { /* shutting down */ }
    }
}
