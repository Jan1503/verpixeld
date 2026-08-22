using System.Text.Json;
using CanvasManagement;
using verpixeld.Configuration;
using verpixeld.Interfaces;
using verpixeld.Layout;
using verpixeld.MediaPlayer;
using Timer = System.Timers.Timer;

namespace verpixeld.Services;

/// <summary>
///     Rotates the content of individual canvases through a per-canvas list of steps (extension + params)
///     on a timer, with an optional fade transition. Each canvas rotates independently; the rest of the
///     display stays put. Complements the scene playlist (which swaps the whole layout).
/// </summary>
public class CanvasRotationService
{
    private readonly Dictionary<string, bool> _advancing = new();
    private readonly Dictionary<string, CanvasRotationConfig> _configs = new(StringComparer.OrdinalIgnoreCase);
    private readonly CanvasManager _cm;
    private readonly ICanvasContentManager _content;
    private readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly Dictionary<string, Timer> _timers = new(StringComparer.OrdinalIgnoreCase);

    // The canvas currently showing rotation-driven USB camera (single device/stream), or null.
    private string? _cameraCanvas;

    // Multi-instance, video-only player so several canvases can play different videos simultaneously.
    private readonly CanvasVideoService _video;

    public CanvasRotationService(CanvasManager cm, ICanvasContentManager content)
    {
        _cm = cm;
        _content = content;
        _video = new CanvasVideoService(cm);
        Load();
    }

    /// <summary>Optional USB camera, wired after construction (used by "camera" content steps).</summary>
    public LocalCameraService? LocalCamera { get; set; }

    public CanvasRotationConfig GetConfig(string canvas)
    {
        lock (_lock)
        {
            return _configs.TryGetValue(canvas, out var c) ? c : new CanvasRotationConfig();
        }
    }

    public bool IsRunning(string canvas)
    {
        lock (_lock)
        {
            return _timers.ContainsKey(canvas);
        }
    }

    /// <summary>Starts every canvas rotation that was left enabled (called once after the layout loads).</summary>
    public void StartIfEnabled()
    {
        lock (_lock)
        {
            foreach (var (canvas, cfg) in _configs)
                if (cfg is { Enabled: true, Steps.Count: > 0 } && !_timers.ContainsKey(canvas) && CanvasIsLive(canvas))
                    StartInternal(canvas);
        }
    }

    /// <summary>
    ///     Stops every running rotation timer without changing Enabled or saved configs.
    ///     Call before a layout rebuild so Advance can't race with canvas teardown.
    /// </summary>
    public void StopAllTimers()
    {
        lock (_lock)
        {
            foreach (var name in _timers.Keys.ToList())
                StopInternal(name);
        }
    }

    /// <summary>
    ///     Stops timers for canvases that are no longer on the display, and (unless rotations are
    ///     suspended for Studio editing) starts enabled rotations whose canvases exist again.
    ///     Configs stay in canvas_rotations.json so they can come back with a later layout.
    /// </summary>
    public void SyncToLiveCanvases()
    {
        List<string> stale;
        lock (_lock)
        {
            stale = _timers.Keys.Where(name => !CanvasIsLive(name)).ToList();
            foreach (var name in stale)
                StopInternal(name);
            _suspendedRunning.RemoveWhere(name => !CanvasIsLive(name));
        }

        foreach (var name in stale)
        {
            _video.Stop(name);
            StopCameraIfActive(name);
        }

        lock (_lock)
        {
            if (_suspended) return;
            foreach (var (canvas, cfg) in _configs)
                if (cfg is { Enabled: true, Steps.Count: > 0 } && !_timers.ContainsKey(canvas) && CanvasIsLive(canvas))
                    StartInternal(canvas);
        }
    }

    /// <summary>Captures the canvas's currently-assigned extension + its live config as a new rotation step.</summary>
    public bool AddCurrentAsStep(string canvas)
    {
        var current = _content.GetContent(canvas);
        if (current?.ExtensionDisplayName == null) return false;

        lock (_lock)
        {
            var cfg = GetOrCreate(canvas);
            cfg.Steps.Add(new RotationStep
            {
                Extension = current.ExtensionDisplayName,
                Config = current.Configuration.Count > 0
                    ? new Dictionary<string, object>(current.Configuration)
                    : null
            });
            Save();
            return true;
        }
    }

    /// <summary>Returns the content items (steps) configured for a canvas.</summary>
    public IReadOnlyList<RotationStep> GetSteps(string canvas)
    {
        lock (_lock)
        {
            return _configs.TryGetValue(canvas, out var c) ? c.Steps.ToList() : new List<RotationStep>();
        }
    }

    /// <summary>Index of the step currently shown on the canvas (-1 if none).</summary>
    public int GetActiveIndex(string canvas)
    {
        lock (_lock)
        {
            return _index.TryGetValue(canvas, out var i) ? i : -1;
        }
    }

    public RotationStep? GetStep(string canvas, int index)
    {
        lock (_lock)
        {
            if (_configs.TryGetValue(canvas, out var c) && index >= 0 && index < c.Steps.Count) return c.Steps[index];
            return null;
        }
    }

    /// <summary>Adds a content item (extension + optional config) to the canvas's list.</summary>
    public void AddStep(string canvas, string extension, Dictionary<string, object>? config)
    {
        lock (_lock)
        {
            var cfg = GetOrCreate(canvas);
            cfg.Steps.Add(new RotationStep { Extension = extension, Config = config });
            Save();
        }
    }

    /// <summary>Adds a media (video) content item to the canvas's list.</summary>
    public void AddMedia(string canvas, string file, bool loop)
    {
        lock (_lock)
        {
            var cfg = GetOrCreate(canvas);
            cfg.Steps.Add(new RotationStep
            {
                Type = "media",
                Extension = "Media",
                Config = new Dictionary<string, object> { ["file"] = file, ["loop"] = loop }
            });
            Save();
        }
    }

    /// <summary>Adds a USB-camera content item. Device/effect are optional per-step overrides.</summary>
    public void AddCamera(string canvas, string? device = null, string? effect = null)
    {
        lock (_lock)
        {
            var cfg = GetOrCreate(canvas);
            var config = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(device)) config["device"] = device;
            if (!string.IsNullOrWhiteSpace(effect)) config["effect"] = effect;
            cfg.Steps.Add(new RotationStep
            {
                Type = "camera",
                Extension = "Camera",
                Config = config.Count > 0 ? config : null
            });
            Save();
        }
    }

    /// <summary>
    ///     Sets an item's config. If that item is currently displayed, push the new values into the
    ///     running extension (same path as the live Params editor) instead of tearing it down and
    ///     constructing a fresh instance — otherwise media extensions restart from the beginning
    ///     every time the parameter page is saved or closed.
    /// </summary>
    public void SetStepConfig(string canvas, int index, Dictionary<string, object>? config)
    {
        RotationStep? step = null;
        var active = false;
        lock (_lock)
        {
            if (!_configs.TryGetValue(canvas, out var c) || index < 0 || index >= c.Steps.Count) return;
            c.Steps[index].Config = config;
            Save();
            step = c.Steps[index];
            active = _index.TryGetValue(canvas, out var i) && i == index;
        }

        if (!active || step == null) return;

        var cfg = ConfigNormalizer.Normalize(config);
        var existing = _content.GetContent(canvas);
        var sameExtension = existing is { ContentType: ContentType.DynamicExtension, ExtensionDisplayName: { } name }
                            && string.Equals(name, step.Extension, StringComparison.OrdinalIgnoreCase);
        if (sameExtension && cfg != null)
            _content.UpdateConfiguration(canvas, cfg);
        else
            _content.AssignExtension(canvas, step.Extension, cfg);
    }

    public void DuplicateStep(string canvas, int index)
    {
        lock (_lock)
        {
            if (!_configs.TryGetValue(canvas, out var c) || index < 0 || index >= c.Steps.Count) return;
            var s = c.Steps[index];
            c.Steps.Insert(index + 1, new RotationStep
            {
                Extension = s.Extension,
                Config = s.Config != null ? new Dictionary<string, object>(s.Config) : null
            });
            Save();
        }
    }

    /// <summary>Applies a step's content to the canvas now (so it can be previewed/tuned).</summary>
    public async Task<bool> ApplyStep(string canvas, int index)
    {
        RotationStep step;
        var alreadyShowing = false;
        lock (_lock)
        {
            if (!_configs.TryGetValue(canvas, out var cfg) || index < 0 || index >= cfg.Steps.Count) return false;
            alreadyShowing = _index.TryGetValue(canvas, out var i) && i == index;
            step = cfg.Steps[index];
            _index[canvas] = index;
        }

        // Opening the parameter page calls apply-step so edits preview live. If this step is
        // already on the canvas, rebuilding it would restart playback (trailers, YouTube, video, …).
        if (alreadyShowing)
        {
            var type = (step.Type ?? "extension").ToLowerInvariant();
            if (type is "media" or "camera")
                return true;

            var existing = _content.GetContent(canvas);
            if (existing is { ContentType: ContentType.DynamicExtension, ExtensionDisplayName: { } name }
                && string.Equals(name, step.Extension, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        await ApplyStepContent(canvas, step);
        return true;
    }

    /// <summary>Overwrites a step with the canvas's current content (capture edits made via the Params editor).</summary>
    public bool UpdateStep(string canvas, int index)
    {
        var current = _content.GetContent(canvas);
        if (current?.ExtensionDisplayName == null) return false;

        lock (_lock)
        {
            if (!_configs.TryGetValue(canvas, out var cfg) || index < 0 || index >= cfg.Steps.Count) return false;
            cfg.Steps[index] = new RotationStep
            {
                Extension = current.ExtensionDisplayName,
                Config = current.Configuration.Count > 0
                    ? new Dictionary<string, object>(current.Configuration)
                    : null
            };
            Save();
            return true;
        }
    }

    public void RemoveStep(string canvas, int index)
    {
        RotationStep? removed = null;
        var wasActive = false;
        var remaining = 0;
        var nextIndex = -1;
        lock (_lock)
        {
            if (!_configs.TryGetValue(canvas, out var cfg)) return;
            if (index < 0 || index >= cfg.Steps.Count) return;
            wasActive = _index.TryGetValue(canvas, out var active) && active == index;
            removed = cfg.Steps[index];
            cfg.Steps.RemoveAt(index);
            remaining = cfg.Steps.Count;
            if (remaining == 0)
            {
                _index[canvas] = -1;
                cfg.Enabled = false;
                StopInternal(canvas);
            }
            else
            {
                var cur = _index.TryGetValue(canvas, out var i) ? i : 0;
                if (cur > index) cur--;
                else if (cur == index) cur = Math.Min(index, remaining - 1);
                if (cur < 0 || cur >= remaining) cur = remaining - 1;
                _index[canvas] = cur;
                nextIndex = cur;
            }

            Save();
        }

        var removedStream = removed != null &&
                            (string.Equals(removed.Type, "media", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(removed.Type, "camera", StringComparison.OrdinalIgnoreCase));

        if (remaining == 0 || wasActive || removedStream)
        {
            _video.Stop(canvas);
            StopCameraIfActive(canvas);
            if (remaining == 0)
            {
                try { _content.StopContent(canvas); } catch { /* canvas may have no extension */ }
                try { _cm.GetCanvasByName(canvas)?.Clear(); } catch { /* ignore */ }
                Console.WriteLine($"[ROTATE] '{canvas}' content cleared (last item removed)");
                return;
            }

            if (nextIndex >= 0)
                ApplyStep(canvas, nextIndex).GetAwaiter().GetResult();
        }
    }

    public void MoveStep(string canvas, int index, int dir)
    {
        lock (_lock)
        {
            if (!_configs.TryGetValue(canvas, out var cfg)) return;
            var j = index + dir;
            if (index < 0 || index >= cfg.Steps.Count || j < 0 || j >= cfg.Steps.Count) return;
            (cfg.Steps[index], cfg.Steps[j]) = (cfg.Steps[j], cfg.Steps[index]);
            Save();
        }
    }

    /// <summary>Updates interval/transition/loop (and optionally enables → starts; disables → stops).</summary>
    public void UpdateSettings(string canvas, int intervalSeconds, CanvasTransition transition, bool loop, bool enabled)
    {
        lock (_lock)
        {
            var cfg = GetOrCreate(canvas);
            cfg.IntervalSeconds = Math.Max(2, intervalSeconds);
            cfg.Transition = transition;
            cfg.Loop = loop;
            cfg.Enabled = enabled;
            Save();
            StopInternal(canvas);
            if (enabled && cfg.Steps.Count > 0 && CanvasIsLive(canvas)) StartInternal(canvas);
        }
    }

    public void Start(string canvas)
    {
        lock (_lock)
        {
            var cfg = GetOrCreate(canvas);
            if (cfg.Steps.Count == 0) return;
            cfg.Enabled = true;
            Save();
            if (CanvasIsLive(canvas)) StartInternal(canvas);
            else Console.WriteLine($"[ROTATE] '{canvas}' not started (canvas not in current layout)");
        }
    }

    public void Stop(string canvas)
    {
        lock (_lock)
        {
            if (_configs.TryGetValue(canvas, out var cfg)) cfg.Enabled = false;
            Save();
            StopInternal(canvas);
        }
    }

    private bool _suspended;
    private readonly HashSet<string> _suspendedRunning = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Stops all rotation timers (remembering which were running) without changing Enabled state.</summary>
    public bool SuspendAll()
    {
        lock (_lock)
        {
            if (_suspended) return _suspendedRunning.Count > 0;
            _suspended = true;
            _suspendedRunning.Clear();
            foreach (var name in _timers.Keys.ToList())
            {
                _suspendedRunning.Add(name);
                StopInternal(name);
            }

            if (_suspendedRunning.Count > 0) Console.WriteLine($"[ROTATE] Suspended {_suspendedRunning.Count} (editing)");
            return _suspendedRunning.Count > 0;
        }
    }

    /// <summary>Restarts the rotations that were running when suspended.</summary>
    public void ResumeAll()
    {
        lock (_lock)
        {
            if (!_suspended) return;
            _suspended = false;
            foreach (var name in _suspendedRunning.ToList())
                if (_configs.TryGetValue(name, out var cfg) && cfg.Steps.Count > 0 && CanvasIsLive(name))
                    StartInternal(name);
                else if (!CanvasIsLive(name))
                    Console.WriteLine($"[ROTATE] '{name}' not resumed (canvas not in current layout)");
            _suspendedRunning.Clear();
        }
    }

    public void Next(string canvas)
    {
        _ = Task.Run(() => Advance(canvas, 1));
    }

    public void Previous(string canvas)
    {
        _ = Task.Run(() => Advance(canvas, -1));
    }

    /// <summary>Drops a canvas's rotation entirely (e.g. when the canvas is removed).</summary>
    public void Forget(string canvas)
    {
        _video.Stop(canvas);
        StopCameraIfActive(canvas);
        lock (_lock)
        {
            StopInternal(canvas);
            if (_configs.Remove(canvas)) Save();
            _index.Remove(canvas);
        }
    }

    /// <summary>Stops and removes every rotation (used when a layout load redefines the whole display).</summary>
    public void ClearAll()
    {
        _video.StopAll();
        if (_cameraCanvas != null) StopCameraIfActive(_cameraCanvas);
        lock (_lock)
        {
            foreach (var name in _timers.Keys.ToList()) StopInternal(name);
            _configs.Clear();
            _index.Clear();
            Save();
        }
    }

    /// <summary>Replaces a canvas's rotation config (from a loaded layout) and starts it if enabled.</summary>
    public void ImportConfig(string canvas, CanvasRotationConfig cfg)
    {
        lock (_lock)
        {
            _configs[canvas] = cfg;
            Save();
            StopInternal(canvas);
            _index[canvas] = -1;
            if (cfg is { Enabled: true, Steps.Count: > 0 } && CanvasIsLive(canvas)) StartInternal(canvas);
        }
    }

    private CanvasRotationConfig GetOrCreate(string canvas)
    {
        if (!_configs.TryGetValue(canvas, out var cfg))
        {
            cfg = new CanvasRotationConfig();
            _configs[canvas] = cfg;
        }

        return cfg;
    }

    private bool CanvasIsLive(string canvas) => _cm.GetCanvasByName(canvas) != null;

    private void StartInternal(string canvas)
    {
        var cfg = GetOrCreate(canvas);
        if (cfg.Steps.Count == 0) return;
        if (!CanvasIsLive(canvas))
        {
            Console.WriteLine($"[ROTATE] '{canvas}' not started (canvas not in current layout)");
            return;
        }
        StopInternal(canvas);
        _index[canvas] = -1;
        var timer = new Timer(Math.Max(2, cfg.IntervalSeconds) * 1000.0) { AutoReset = true };
        timer.Elapsed += (_, _) => Advance(canvas, 1);
        _timers[canvas] = timer;
        timer.Start();
        Console.WriteLine(
            $"[ROTATE] '{canvas}': {cfg.Steps.Count} steps, {cfg.IntervalSeconds}s, {cfg.Transition}");
        _ = Task.Run(() => Advance(canvas, 1)); // show first step immediately
    }

    private void StopInternal(string canvas)
    {
        if (_timers.Remove(canvas, out var timer))
        {
            timer.Stop();
            timer.Dispose();
            Console.WriteLine($"[ROTATE] '{canvas}' stopped");
        }
    }

    private void Advance(string canvas, int dir)
    {
        lock (_lock)
        {
            if (_advancing.TryGetValue(canvas, out var busy) && busy) return;
            _advancing[canvas] = true;
        }

        try
        {
            if (!CanvasIsLive(canvas))
            {
                lock (_lock)
                {
                    StopInternal(canvas);
                    _suspendedRunning.Remove(canvas);
                }
                _video.Stop(canvas);
                StopCameraIfActive(canvas);
                return;
            }

            RotationStep step;
            CanvasTransition transition;
            lock (_lock)
            {
                if (!_configs.TryGetValue(canvas, out var cfg) || cfg.Steps.Count == 0) return;
                var i = (_index.TryGetValue(canvas, out var cur) ? cur : -1) + dir;
                if (i >= cfg.Steps.Count)
                {
                    if (!cfg.Loop)
                    {
                        StopInternal(canvas);
                        return;
                    }

                    i = 0;
                }

                if (i < 0) i = cfg.Steps.Count - 1;
                _index[canvas] = i;
                step = cfg.Steps[i];
                transition = cfg.Transition;
            }

            ShowStep(canvas, step, transition).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ROTATE] '{canvas}' advance error: {ex.Message}");
        }
        finally
        {
            lock (_lock)
            {
                _advancing[canvas] = false;
            }
        }
    }

    private async Task ShowStep(string canvas, RotationStep step, CanvasTransition transition)
    {
        var surface = _cm.GetCanvasByName(canvas);
        var fade = transition == CanvasTransition.Fade && surface != null;
        var target = surface?.Opacity ?? 1f;

        if (fade) await FadeTo(surface!, 0f, 200);

        await ApplyStepContent(canvas, step);

        if (fade)
        {
            surface!.Opacity = 0f;
            await FadeTo(surface, target <= 0f ? 1f : target, 200);
        }
    }

    /// <summary>Applies a step's content to a canvas: an extension, or server-side media playback.</summary>
    private async Task ApplyStepContent(string canvas, RotationStep step)
    {
        var cfg = ConfigNormalizer.Normalize(step.Config);
        var type = (step.Type ?? "extension").ToLowerInvariant();

        if (type == "media")
        {
            var file = cfg != null && cfg.TryGetValue("file", out var f) ? f?.ToString() : null;
            var loop = !(cfg != null && cfg.TryGetValue("loop", out var l) && l is bool b && b == false);
            if (string.IsNullOrWhiteSpace(file)) return;

            StopCameraIfActive(canvas);
            _content.StopContent(canvas); // clear any extension occupying the canvas
            var surface = _cm.GetCanvasByName(canvas);
            if (surface == null) return;
            _video.Play(surface, Path.Combine(AppPaths.VideosDir, file), loop);
            return;
        }

        if (type == "camera" && LocalCamera != null)
        {
            _video.Stop(canvas);
            _content.StopContent(canvas);
            var surface = _cm.GetCanvasByName(canvas);
            if (surface != null)
            {
                var device = cfg != null && cfg.TryGetValue("device", out var dv) ? dv?.ToString() : null;
                var effect = cfg != null && cfg.TryGetValue("effect", out var ef) ? ef?.ToString() : null;
                LocalCamera.StopStream(); // ensure no other camera stream is running
                if (LocalCamera.StartStreamOnCanvas(surface, device, effect)) _cameraCanvas = canvas;
            }

            return;
        }

        // Extension step — stop any video/camera this canvas was showing first.
        _video.Stop(canvas);
        StopCameraIfActive(canvas);
        _content.AssignExtension(canvas, step.Extension, cfg);
    }

    private void StopCameraIfActive(string canvas)
    {
        if (LocalCamera != null && string.Equals(_cameraCanvas, canvas, StringComparison.OrdinalIgnoreCase))
        {
            try { LocalCamera.StopStream(); } catch { /* best-effort */ }
            _cameraCanvas = null;
        }
    }

    private static async Task FadeTo(Canvas canvas, float target, int totalMs)
    {
        const int steps = 10;
        var start = canvas.Opacity;
        for (var i = 1; i <= steps; i++)
        {
            canvas.Opacity = start + (target - start) * (i / (float)steps);
            await Task.Delay(Math.Max(1, totalMs / steps));
        }

        canvas.Opacity = target;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(AppPaths.CanvasRotations)) return;
            var data = JsonSerializer.Deserialize<Dictionary<string, CanvasRotationConfig>>(
                File.ReadAllText(AppPaths.CanvasRotations));
            if (data == null) return;
            foreach (var (k, v) in data) _configs[k] = v;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ROTATE] load config failed: {ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(AppPaths.CanvasRotations,
                JsonSerializer.Serialize(_configs, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ROTATE] save config failed: {ex.Message}");
        }
    }
}
