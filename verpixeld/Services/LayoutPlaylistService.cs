using System.Text.Json;
using CanvasManagement;
using verpixeld.Configuration;
using verpixeld.Interfaces;
using verpixeld.Layout;
using Timer = System.Timers.Timer;

namespace verpixeld.Services;

/// <summary>
///     Cycles through an ordered list of saved layouts ("scenes") on a timer, with an optional fade transition.
///     Complements the time-of-day scheduler: the scheduler decides which layout is active at a given time,
///     the playlist rotates between layouts continuously.
/// </summary>
public class LayoutPlaylistService
{
    private readonly CanvasManager _cm;
    private readonly object _lock = new();
    private readonly ILayoutLoaderService _loader;
    private readonly LayoutStorageManager _storage;

    private volatile bool _advancing;
    private int _index = -1;
    private Timer? _timer;

    public LayoutPlaylistService(CanvasManager cm, LayoutStorageManager storage, ILayoutLoaderService loader)
    {
        _cm = cm;
        _storage = storage;
        _loader = loader;
        Load();
    }

    private bool _suspended;
    private bool _wasRunningBeforeSuspend;

    public PlaylistConfiguration Config { get; private set; } = new();
    public bool IsRunning { get; private set; }
    public string? CurrentLayout { get; private set; }

    /// <summary>Starts the playlist on boot if it was enabled.</summary>
    public void StartIfEnabled()
    {
        if (Config is { Enabled: true, Layouts.Count: > 0 }) Start();
    }

    public void Configure(PlaylistConfiguration cfg)
    {
        lock (_lock)
        {
            Config = cfg ?? new PlaylistConfiguration();
            Save();
            StopInternal();
            if (Config is { Enabled: true, Layouts.Count: > 0 }) StartInternal();
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            Config.Enabled = true;
            Save();
            StartInternal();
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            Config.Enabled = false;
            Save();
            StopInternal();
        }
    }

    /// <summary>
    ///     Temporarily stops the rotation timer WITHOUT changing the persisted Enabled state (used while the
    ///     Layout Editor is open so edits aren't wiped by the next scene swap). Returns true if it was running.
    /// </summary>
    public bool Suspend()
    {
        lock (_lock)
        {
            if (_suspended) return _wasRunningBeforeSuspend;
            _suspended = true;
            _wasRunningBeforeSuspend = IsRunning;
            StopInternal();
            if (_wasRunningBeforeSuspend) Console.WriteLine("[PLAYLIST] Suspended (editing)");
            return _wasRunningBeforeSuspend;
        }
    }

    /// <summary>Resumes rotation if it was running when suspended.</summary>
    public void Resume()
    {
        lock (_lock)
        {
            if (!_suspended) return;
            _suspended = false;
            if (_wasRunningBeforeSuspend && Config is { Enabled: true, Layouts.Count: > 0 })
            {
                Console.WriteLine("[PLAYLIST] Resumed after editing");
                StartInternal();
            }
        }
    }

    public void Next()
    {
        _ = Task.Run(() => Advance(1));
    }

    public void Previous()
    {
        _ = Task.Run(() => Advance(-1));
    }

    private void StartInternal()
    {
        if (Config.Layouts.Count == 0) return;
        _timer?.Stop();
        _timer?.Dispose();
        _index = -1;
        _timer = new Timer(Math.Max(2, Config.IntervalSeconds) * 1000.0) { AutoReset = true };
        _timer.Elapsed += (_, _) => Advance(1);
        _timer.Start();
        IsRunning = true;
        Console.WriteLine(
            $"[PLAYLIST] Started: {Config.Layouts.Count} layouts, {Config.IntervalSeconds}s, {Config.Transition}");
        _ = Task.Run(() => Advance(1)); // show the first scene immediately
    }

    private void StopInternal()
    {
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
        IsRunning = false;
        Console.WriteLine("[PLAYLIST] Stopped");
    }

    private void Advance(int dir)
    {
        if (_advancing) return; // don't overlap a heavy layout swap
        _advancing = true;
        try
        {
            string name;
            lock (_lock)
            {
                if (Config.Layouts.Count == 0) return;
                _index += dir;
                if (_index >= Config.Layouts.Count)
                {
                    if (!Config.Loop)
                    {
                        StopInternal();
                        return;
                    }

                    _index = 0;
                }

                if (_index < 0) _index = Config.Layouts.Count - 1;
                name = Config.Layouts[_index];
            }

            ShowLayout(name).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PLAYLIST] advance error: {ex.Message}");
        }
        finally
        {
            _advancing = false;
        }
    }

    private async Task ShowLayout(string name)
    {
        var layout = _storage.LoadLayout(name);
        if (layout == null)
        {
            Console.WriteLine($"[PLAYLIST] layout '{name}' not found — skipping");
            return;
        }

        var fade = Config.Transition == PlaylistTransition.Fade;
        if (fade) await FadeTo(0f, 250);

        await _loader.LoadLayoutAsync(layout, "PLAYLIST");
        CurrentLayout = name;

        if (fade)
        {
            // LoadLayoutAsync applied the new layout's brightness; fade up to it from black.
            var target = _cm.Brightness;
            if (target <= 0f) target = 1f;
            _cm.Brightness = 0f;
            await FadeTo(target, 250);
        }

        Console.WriteLine($"[PLAYLIST] -> {name}");
    }

    private async Task FadeTo(float target, int totalMs)
    {
        const int steps = 12;
        var start = _cm.Brightness;
        for (var i = 1; i <= steps; i++)
        {
            _cm.Brightness = start + (target - start) * (i / (float)steps);
            await Task.Delay(Math.Max(1, totalMs / steps));
        }

        _cm.Brightness = target;
    }

    private void Load()
    {
        try
        {
            if (File.Exists(AppPaths.Playlist))
                Config = JsonSerializer.Deserialize<PlaylistConfiguration>(File.ReadAllText(AppPaths.Playlist))
                         ?? new PlaylistConfiguration();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PLAYLIST] load config failed: {ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(AppPaths.Playlist,
                JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PLAYLIST] save config failed: {ex.Message}");
        }
    }
}
