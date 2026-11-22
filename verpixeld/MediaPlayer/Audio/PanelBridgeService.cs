using PixTone;
using verpixeld.Configuration;
using verpixeld.Services;
using verpixeld.WebApi;

namespace verpixeld.MediaPlayer.Audio;

/// <summary>
///     The audio board's touch panel as a remote for verpixeld. Sends the play state, the
///     favorites / radio / layout lists and the settings (shuffle, repeat, wall, sleep timer,
///     A/V sync), and carries out the buttons pressed on the panel.
///     Favorites and layouts start through their own API endpoints on this host, so the panel
///     behaves exactly like the web page.
/// </summary>
public sealed class PanelBridgeService
{
    private readonly PixToneOutputService _board;
    private readonly MediaPlayerService _media;
    private readonly FavoritesService _favorites;
    private readonly RadioService _radio;
    private readonly SleepTimerService _sleep;
    private readonly AlarmService _alarms;
    private readonly SongLogService _songs;
    private readonly IAudioVisualizerService _visualizer;
    private readonly EndpointContext _ctx;
    private string? _visualCanvas;
    private readonly HttpClient _self;
    private readonly object _gate = new();

    private readonly ListState[] _lists = { new(PanelList.Favorites), new(PanelList.Radio), new(PanelList.Layouts) };
    private DateTime _nextLists = DateTime.MinValue;
    private string? _activeFavorite;
    private string? _activeLayout;
    private float _wallBrightness = 0.5f;
    private CancellationTokenSource? _syncApply;

    private sealed class ListState
    {
        public ListState(PanelList id) => Id = id;
        public PanelList Id { get; }
        public byte Generation { get; set; }
        public string Signature { get; set; } = "\0";
        /// <summary>Ids by generation, so a key that crossed a list update still finds its entry.</summary>
        public Dictionary<byte, List<string>> Ids { get; } = new();
    }

    public PanelBridgeService(PixToneOutputService board, MediaPlayerService media, FavoritesService favorites,
        RadioService radio, SleepTimerService sleep, AlarmService alarms, SongLogService songs,
        IAudioVisualizerService visualizer, EndpointContext ctx, IConfiguration configuration)
    {
        _board = board;
        _media = media;
        _favorites = favorites;
        _radio = radio;
        _sleep = sleep;
        _alarms = alarms;
        _songs = songs;
        _visualizer = visualizer;
        _ctx = ctx;
        var port = configuration.GetSection(WebServerOptions.SectionName).GetValue("HttpPort", 5000);
        // With HTTPS on, plain HTTP is redirected; the header is the server's own way to stay on HTTP.
        _self = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        _self.DefaultRequestHeaders.Add("X-Allow-HTTP", "1");
        if (_ctx.CanvasManager.Brightness > 0.01f) _wallBrightness = _ctx.CanvasManager.Brightness;

        _board.PlayStateSource = PlayState;
        _board.PanelPackets = Packets;
        _board.PanelKeyPressed += OnKey;
        _board.SuppressPreview = AudioOnly;
        _visualizer.ExternalBands = () => _board.LatestBands;
        _visualizer.ExternalAvailable = () => _board.IsSelected && _board.BoardOnline;
        _radio.StationsChanged += Kick;
        _radio.NowChanged += Kick;
        _sleep.Changed += Kick;
        _alarms.Changed += Kick;
        Console.WriteLine("[PANEL] Audio board panel bridge ready");
    }

    private void Kick()
    {
        lock (_gate) _nextLists = DateTime.MinValue;
        _board.KickPanel();
    }

    // ---- to the board -------------------------------------------------------------------

    /// <summary>Music or radio: the panel screensaver shows the cover, and the wall picture stays home.</summary>
    public bool AudioOnly() => _media.IsRunning && (_media.IsRadioPlaying || _media.IsAudioPlayback);

    private PixToneOutputService.PanelPlayInfo PlayState()
    {
        var flags = PlayFlags.None;
        if (_media.IsRadioPlaying && _radio.Stations.Count > 1)
            flags |= PlayFlags.HasPrevious | PlayFlags.HasNext;
        else
        {
            if (_media.HasPreviousTrack) flags |= PlayFlags.HasPrevious;
            if (_media.HasNextTrack) flags |= PlayFlags.HasNext;
        }
        if (!_media.IsRunning)
            return new(PixTone.PlayState.Stopped, flags, TimeSpan.Zero, TimeSpan.Zero);
        var duration = _media.IsRadioPlaying ? TimeSpan.Zero : _media.VideoDuration;
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        if (_media.SeekingSupported && duration > TimeSpan.Zero) flags |= PlayFlags.CanSeek;
        var position = _media.VideoPosition;
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        return new(_media.IsPaused ? PixTone.PlayState.Paused : PixTone.PlayState.Playing, flags, position, duration);
    }

    private IEnumerable<byte[]> Packets()
    {
        var packets = new List<byte[]>();
        var flags = PanelFlags.None;
        if (_media.ShuffleMode) flags |= PanelFlags.Shuffle;
        if (_media.RepeatMode) flags |= PanelFlags.Repeat;
        var brightness = _ctx.CanvasManager.Brightness;
        if (brightness > 0.005f) flags |= PanelFlags.WallOn;
        if (_media.IsRadioPlaying) flags |= PanelFlags.Radio;
        if (AudioOnly()) flags |= PanelFlags.AudioOnly;
        if (_visualizer.IsRunning && _visualizer.UsingExternal) flags |= PanelFlags.Visualizer;
        var alarm = _alarms.PanelAlarm;
        if (alarm is { Enabled: true }) flags |= PanelFlags.Alarm;
        packets.Add(PixToneProtocol.SettingsPacket(flags, (int)Math.Round(brightness * 100), _sleep.MinutesLeft,
            _media.AudioSyncOffsetMs, alarm == null ? -1 : alarm.Hour * 60 + alarm.Minute, alarm?.Days ?? 0));

        var now = DateTime.UtcNow;
        bool resend;
        lock (_gate)
        {
            resend = now >= _nextLists;
            if (resend) _nextLists = now.AddSeconds(5);
        }
        if (!_media.IsRunning) _activeFavorite = null;

        var favs = _favorites.Favorites.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(PixToneProtocol.ListMax).ToList();
        AddList(packets, 0, favs.Select(f => f.Id).ToList(), favs.Select(f => f.Name).ToList(),
            _activeFavorite == null ? -1 : favs.FindIndex(f => f.Id == _activeFavorite), resend);

        var stations = _radio.Stations.Take(PixToneProtocol.ListMax).ToList();
        AddList(packets, 1, stations.Select(s => s.Id).ToList(), stations.Select(s => s.Name).ToList(),
            _radio.CurrentIndex, resend);

        List<string> layouts;
        try
        {
            layouts = _ctx.LayoutStorageManager.GetAllLayouts().Select(l => l.Name)
                .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).Take(PixToneProtocol.ListMax).ToList();
        }
        catch
        {
            layouts = new List<string>();
        }
        AddList(packets, 2, layouts, layouts, _activeLayout == null ? -1 : layouts.IndexOf(_activeLayout), resend);
        return packets;
    }

    private void AddList(List<byte[]> packets, int slot, List<string> ids, List<string> names, int active, bool resend)
    {
        var list = _lists[slot];
        var signature = string.Join("\u0001", ids) + "\u0002" + string.Join("\u0001", names) + "\u0002" + active;
        bool changed;
        byte gen;
        lock (_gate)
        {
            changed = signature != list.Signature;
            if (changed)
            {
                list.Signature = signature;
                var idsChanged = !list.Ids.TryGetValue(list.Generation, out var prev) || !prev.SequenceEqual(ids);
                if (idsChanged)
                {
                    list.Generation++;
                    list.Ids[list.Generation] = ids;
                    // Keep the last few generations only.
                    foreach (var old in list.Ids.Keys.Where(k => (byte)(list.Generation - k) > 4).ToList())
                        list.Ids.Remove(old);
                }
            }
            gen = list.Generation;
        }
        if (changed || resend) packets.AddRange(PixToneProtocol.ListPackets(list.Id, gen, names, active));
    }

    private string? IdFor(PanelList id, byte generation, int index)
    {
        lock (_gate)
        {
            var list = _lists[(int)id - 1];
            if (!list.Ids.TryGetValue(generation, out var ids) && !list.Ids.TryGetValue(list.Generation, out ids))
                return null;
            return index >= 0 && index < ids.Count ? ids[index] : null;
        }
    }

    // ---- from the board -----------------------------------------------------------------

    private void OnKey(PanelKey key, ushort arg, byte generation)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await HandleAsync(key, arg, generation);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PANEL] {key}: {ex.Message}");
            }
            Kick();
        });
    }

    private async Task HandleAsync(PanelKey key, ushort arg, byte generation)
    {
        switch (key)
        {
            case PanelKey.PlayPause:
                if (_media.IsRunning) _media.TogglePause();
                else if (_radio.LastStation is { } last) await _radio.PlayAsync(last);
                break;
            case PanelKey.Stop:
                if (_alarms.Ringing != null) _alarms.StopRinging();
                if (_media.IsRadioPlaying) await _radio.StopAsync();
                else await _media.StopAsync();
                break;
            case PanelKey.Next:
                if (_media.IsRadioPlaying) await _radio.StepAsync(1);
                else await _media.PlayNextAsync();
                break;
            case PanelKey.Previous:
                if (_media.IsRadioPlaying) await _radio.StepAsync(-1);
                else await _media.PlayPreviousAsync();
                break;
            case PanelKey.Seek:
                if (_media.IsRunning && _media.SeekingSupported && _media.VideoDuration > TimeSpan.Zero)
                    await _media.SeekPercentAsync(Math.Clamp(arg / 10.0, 0, 100));
                break;
            case PanelKey.Shuffle:
                _media.ShuffleMode = !_media.ShuffleMode;
                break;
            case PanelKey.Repeat:
                _media.RepeatMode = !_media.RepeatMode;
                break;
            case PanelKey.Sleep:
                _sleep.Cycle();
                break;
            case PanelKey.Sync:
                SetSync(arg - 2000);
                break;
            case PanelKey.Brightness:
            {
                var b = Math.Clamp(arg, (ushort)0, (ushort)100) / 100f;
                _ctx.CanvasManager.Brightness = b;
                if (b > 0.01f) _wallBrightness = b;
                Console.WriteLine($"[PANEL] Wall brightness {arg}%");
                break;
            }
            case PanelKey.WallPower:
            {
                var on = _ctx.CanvasManager.Brightness > 0.005f;
                if (on) _wallBrightness = _ctx.CanvasManager.Brightness;
                _ctx.CanvasManager.Brightness = on ? 0f : Math.Max(0.05f, _wallBrightness);
                Console.WriteLine($"[PANEL] Wall {(on ? "off" : "on")}");
                break;
            }
            case PanelKey.Select:
                await SelectAsync((PanelList)(arg >> 12), arg & 0x0FFF, generation);
                break;
            case PanelKey.Visualizer:
                await ToggleVisualizerAsync();
                break;
            case PanelKey.Alarm:
                _alarms.TogglePanelAlarm();
                break;
            case PanelKey.AlarmTime:
                _alarms.SetPanelTime(arg);
                break;
            case PanelKey.SaveTitle:
                _songs.SaveCurrent();
                break;
        }
    }

    private async Task SelectAsync(PanelList list, int index, byte generation)
    {
        var id = IdFor(list, generation, index);
        if (id == null)
        {
            Console.WriteLine($"[PANEL] {list} entry {index} is gone");
            return;
        }
        switch (list)
        {
            case PanelList.Favorites:
                _activeFavorite = id;
                await PostSelf($"/api/favorites/{Uri.EscapeDataString(id)}/play");
                break;
            case PanelList.Radio:
                await _radio.PlayAsync(id);
                break;
            case PanelList.Layouts:
                _activeLayout = id;
                await PostSelf($"/api/layout/load/{Uri.EscapeDataString(id)}");
                break;
        }
    }

    private async Task PostSelf(string path)
    {
        using var resp = await _self.PostAsync(path, null);
        var body = await resp.Content.ReadAsStringAsync();
        if (body.Length > 160) body = body[..160];
        Console.WriteLine($"[PANEL] {path} → {(int)resp.StatusCode} {body}");
    }

    /// <summary>For Home Assistant: the same toggle as the panel's SPECTRUM button.</summary>
    public void ToggleVisualizerFromOutside()
    {
        _ = Task.Run(async () =>
        {
            try { await ToggleVisualizerAsync(); }
            catch (Exception ex) { Console.WriteLine("[PANEL] spectrum: " + ex.Message); }
            Kick();
        });
    }

    /// <summary>The wall spectrum from the board's FFT, on the canvas used last, else Main, else the first.</summary>
    private async Task ToggleVisualizerAsync()
    {
        if (_visualizer.IsRunning)
        {
            await _visualizer.StopAsync();
            Console.WriteLine("[PANEL] Wall spectrum off");
            return;
        }
        var canvases = _ctx.LayoutManager.GetAllCanvases()
            .Where(c => c.Canvas != null && !SystemOverlayCanvases.IsSystem(c.Name)).ToList();
        if (canvases.Count == 0)
        {
            Console.WriteLine("[PANEL] No canvas for the spectrum");
            return;
        }
        var pick = canvases.FirstOrDefault(c => c.Name == _visualCanvas);
        if (pick.Canvas == null) pick = canvases.FirstOrDefault(c => c.Name == "Main");
        if (pick.Canvas == null) pick = canvases[0];
        _visualCanvas = pick.Name;
        var ok = await _visualizer.StartAsync(pick.Canvas, pick.Name);
        Console.WriteLine(ok ? $"[PANEL] Wall spectrum on {pick.Name}" : "[PANEL] Wall spectrum could not start");
    }

    /// <summary>
    ///     A/V sync from the panel. The offset takes effect when FFmpeg restarts; while something
    ///     plays, a quiet seek to the same position does that once the taps have stopped.
    /// </summary>
    private void SetSync(int ms)
    {
        var old = _media.AudioSyncOffsetMs;
        _media.AudioSyncOffsetMs = Math.Clamp(ms, -2000, 2000);
        Console.WriteLine($"[PANEL] A/V sync {_media.AudioSyncOffsetMs} ms");
        if (old == _media.AudioSyncOffsetMs) return;
        _syncApply?.Cancel();
        var cts = new CancellationTokenSource();
        _syncApply = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(700, cts.Token);
                if (_media.IsRunning && _media.SeekingSupported && _media.VideoPosition.TotalSeconds > 0.5)
                    await _media.SeekAsync(_media.VideoPosition);
            }
            catch (OperationCanceledException) { }
        });
    }
}
