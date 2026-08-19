using System.Text.Json;
using CanvasManagement;
using SkiaSharp;
using verpixeld.Configuration;
using verpixeld.MediaPlayer;

namespace verpixeld.Services;

/// <summary>
///     Service for camera motion alerts.
///     When triggered, pauses current media playback, shows the camera stream on a
///     high-priority canvas, and auto-dismisses after a configurable timeout.
///     Uses <see cref="FfmpegFrameStreamer"/> for the FFmpeg-to-canvas pipeline.
/// </summary>
public class AlertService : IDisposable
{
    private readonly CanvasManager _canvasManager;
    private readonly MediaPlayerService _mediaService;
    private readonly string _configPath;

    private readonly Random _random = new();
    
    // Synchronization: protects all shared state
    private readonly object _stateLock = new();
    
    // Alert state
    private Canvas? _alertCanvas;
    private FfmpegFrameStreamer? _streamer;
    private CancellationTokenSource? _animationCts;
    private Task? _animationTask;
    private Timer? _dismissTimer;
    private DateTime _lastTriggerTime;
    private bool _mediaPausedByUs;
    
    // Display dimensions
    private int _width;
    private int _height;
    
    // Configuration
    public bool IsActive { get; private set; }
    public string? StreamUrl { get; private set; }
    public int TimeoutSeconds { get; set; } = 30;
    public string ScaleFilter { get; set; } = "area";

    /// <summary>Remaining seconds before auto-dismiss (0 if not active).</summary>
    public int RemainingSeconds
    {
        get
        {
            lock (_stateLock)
            {
                if (!IsActive) return 0;
                var elapsed = (DateTime.UtcNow - _lastTriggerTime).TotalSeconds;
                return Math.Max(0, (int)(TimeoutSeconds - elapsed));
            }
        }
    }

    public AlertService(CanvasManager canvasManager, MediaPlayerService mediaService, int width, int height)
    {
        _canvasManager = canvasManager;
        _mediaService = mediaService;
        _width = width;
        _height = height;

        _configPath = AppPaths.AlertConfig;
        LoadConfig();
        Console.WriteLine($"[ALERT] Initialized for {width}x{height} display");
    }

    /// <summary>Trigger the camera alert. If already active, just resets the timeout timer.</summary>
    public void TriggerAlert()
    {
        lock (_stateLock)
        {
            if (string.IsNullOrEmpty(StreamUrl))
            {
                Console.WriteLine("[ALERT] No stream URL configured, ignoring trigger");
                return;
            }

            _lastTriggerTime = DateTime.UtcNow;

            if (IsActive)
            {
                Console.WriteLine("[ALERT] Re-triggered, resetting timeout timer");
                ResetTimer();
                return;
            }

            Console.WriteLine($"[ALERT] Triggered! Showing camera stream for {TimeoutSeconds}s");
            IsActive = true;
            _mediaPausedByUs = false;

            // Pause current media playback if running
            if (_mediaService.IsRunning && !_mediaService.IsPaused)
            {
                Console.WriteLine("[ALERT] Pausing current media playback");
                _mediaService.TogglePause();
                _mediaPausedByUs = true;
            }

            // Create a high-priority canvas (z-order 300)
            _alertCanvas = _canvasManager.GetCanvas(0, 0, _width, _height, 300, "CameraAlert");
            _alertCanvas.Show();

            // Start connecting animation
            _animationCts = new CancellationTokenSource();
            _animationTask = Task.Run(() => RunConnectingAnimation(_animationCts.Token));

            // Start the FFmpeg frame streamer
            var isRtsp = StreamUrl!.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase);
            var inputOpts = isRtsp
                ? "-rtsp_transport tcp -fflags nobuffer -flags low_delay -analyzeduration 500000 -probesize 500000 "
                : "-reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 5 -fflags nobuffer -analyzeduration 500000 -probesize 500000 ";

            var ffmpegArgs = $"-hide_banner -loglevel warning {inputOpts}" +
                             $"-i \"{StreamUrl}\" " +
                             $"-f rawvideo -pix_fmt rgb24 " +
                             $"-vf \"scale={_width}:{_height}:flags={ScaleFilter}\" " +
                             $"-an pipe:1";

            _streamer = new FfmpegFrameStreamer(_width, _height);
            _streamer.OnConnected = () =>
            {
                // Stop the connecting animation once stream is live
                try { _animationCts?.Cancel(); } catch { }
            };
            _streamer.Start(ffmpegArgs, _alertCanvas, displayFps: 20, logPrefix: "[ALERT]");

            // Start the auto-dismiss timer
            ResetTimer();
        }
    }

    /// <summary>Dismiss the alert and return to normal state.</summary>
    public void DismissAlert()
    {
        FfmpegFrameStreamer? streamer;
        Task? animationTask;
        bool shouldResume;
        Canvas? canvas;

        lock (_stateLock)
        {
            if (!IsActive) return;

            Console.WriteLine("[ALERT] Dismissing alert");
            IsActive = false;

            _dismissTimer?.Dispose();
            _dismissTimer = null;
            
            try { _animationCts?.Cancel(); } catch { }
            
            streamer = _streamer;
            animationTask = _animationTask;
            shouldResume = _mediaPausedByUs && _mediaService.IsPaused;
            canvas = _alertCanvas;
            
            _streamer = null;
            _animationTask = null;
            _alertCanvas = null;
            _mediaPausedByUs = false;
        }

        // Stop streamer (outside lock)
        streamer?.Stop("[ALERT]");
        streamer?.Dispose();

        try
        {
            if (animationTask != null) animationTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException) { }

        // Remove canvas
        if (canvas != null)
        {
            try
            {
                canvas.Clear();
                canvas.Hide();
                _canvasManager.RemoveCanvas(canvas);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ALERT] Error removing canvas: {ex.Message}");
            }
        }

        // Resume media if we paused it
        if (shouldResume)
        {
            Console.WriteLine("[ALERT] Resuming media playback");
            _mediaService.TogglePause();
        }

        Console.WriteLine("[ALERT] Dismissed, back to normal");
    }

    /// <summary>Configure the alert stream URL and timeout.</summary>
    public void Configure(string? streamUrl, int? timeoutSeconds, string? scaleFilter)
    {
        if (streamUrl != null)
            StreamUrl = streamUrl;
        if (timeoutSeconds.HasValue)
            TimeoutSeconds = Math.Clamp(timeoutSeconds.Value, 5, 600);
        if (scaleFilter != null)
            ScaleFilter = scaleFilter;

        SaveConfig();
        Console.WriteLine($"[ALERT] Configured: URL={StreamUrl}, Timeout={TimeoutSeconds}s, Scale={ScaleFilter}");
    }

    // ── Connecting animation (runs until first frame arrives) ──

    private async Task RunConnectingAnimation(CancellationToken ct)
    {
        try
        {
            var frame = 0;
            var scanlinePositions = new int[3];
            for (var i = 0; i < scanlinePositions.Length; i++)
                scanlinePositions[i] = _random.Next(_height);
            
            while (!ct.IsCancellationRequested)
            {
                var canvas = _alertCanvas;
                if (canvas == null) break;
                
                try
                {
                    canvas.DrawRect(0, 0, _width, _height, new SKColor(8, 2, 2), SKPaintStyle.Fill);
                    
                    for (var s = 0; s < scanlinePositions.Length; s++)
                    {
                        scanlinePositions[s] = (scanlinePositions[s] + 1 + s) % _height;
                        var scanY = scanlinePositions[s];
                        var scanAlpha = (byte)(30 + s * 15);
                        canvas.DrawRect(0, scanY, _width, 1, new SKColor(255, 0, 0, scanAlpha), SKPaintStyle.Fill);
                    }
                    
                    var bracketLen = Math.Min(8, _width / 6);
                    var bracketColor = new SKColor(255, 60, 60, (byte)(140 + (int)(Math.Sin(frame * 0.15) * 80)));
                    canvas.DrawRect(0, 0, bracketLen, 1, bracketColor, SKPaintStyle.Fill);
                    canvas.DrawRect(0, 0, 1, bracketLen, bracketColor, SKPaintStyle.Fill);
                    canvas.DrawRect(_width - bracketLen, 0, bracketLen, 1, bracketColor, SKPaintStyle.Fill);
                    canvas.DrawRect(_width - 1, 0, 1, bracketLen, bracketColor, SKPaintStyle.Fill);
                    canvas.DrawRect(0, _height - 1, bracketLen, 1, bracketColor, SKPaintStyle.Fill);
                    canvas.DrawRect(0, _height - bracketLen, 1, bracketLen, bracketColor, SKPaintStyle.Fill);
                    canvas.DrawRect(_width - bracketLen, _height - 1, bracketLen, 1, bracketColor, SKPaintStyle.Fill);
                    canvas.DrawRect(_width - 1, _height - bracketLen, 1, bracketLen, bracketColor, SKPaintStyle.Fill);
                    
                    var alertPulse = (byte)(180 + (int)(Math.Sin(frame * 0.2) * 75));
                    var glowAlpha = (byte)(20 + (int)(Math.Sin(frame * 0.2) * 20));
                    canvas.DrawRect(0, 0, _width, 14, new SKColor(255, 0, 0, glowAlpha), SKPaintStyle.Fill);
                    canvas.DrawBdfText("! ALERT !", 2, 2, new SKColor(255, alertPulse, alertPulse));
                    
                    canvas.DrawBdfText("CAM STREAM", 2, 20, new SKColor(200, 200, 200));
                    
                    var dots = new string('.', (frame / 8) % 4);
                    canvas.DrawBdfText($"Connecting{dots}", 2, 38, new SKColor(100, 200, 255));
                    
                    var barCount = 4;
                    var barMaxH = 8;
                    var barW = 2;
                    var barGap = 1;
                    var barsStartX = _width - (barCount * (barW + barGap)) - 4;
                    var activeBar = (frame / 6) % (barCount + 1);
                    for (var b = 0; b < barCount; b++)
                    {
                        var barH = 2 + b * (barMaxH / barCount);
                        var bx = barsStartX + b * (barW + barGap);
                        var by = _height - 6 - barH;
                        var barActive = b < activeBar;
                        var barCol = barActive 
                            ? new SKColor(0, 255, 100) 
                            : new SKColor(40, 40, 40);
                        canvas.DrawRect(bx, by, barW, barH, barCol, SKPaintStyle.Fill);
                    }
                    
                    var timeStr = DateTime.Now.ToString("HH:mm:ss");
                    var timeColor = new SKColor(180, 180, 180, (byte)(180 + (int)(Math.Sin(frame * 0.3) * 40)));
                    canvas.DrawBdfText(timeStr, 2, _height - 22, timeColor);
                    
                    var lineProgress = (frame * 2) % _width;
                    canvas.DrawRect(0, _height - 2, lineProgress, 1, new SKColor(255, 0, 0, 120), SKPaintStyle.Fill);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                
                frame++;
                await Task.Delay(50, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[ALERT] Connecting animation error: {ex.Message}");
        }
    }

    private void ResetTimer()
    {
        _dismissTimer?.Dispose();
        _dismissTimer = new Timer(_ =>
        {
            Console.WriteLine("[ALERT] Timeout expired, auto-dismissing");
            DismissAlert();
        }, null, TimeoutSeconds * 1000, Timeout.Infinite);
    }

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                var config = JsonSerializer.Deserialize<AlertConfig>(json);
                if (config != null)
                {
                    StreamUrl = config.StreamUrl;
                    TimeoutSeconds = config.TimeoutSeconds;
                    ScaleFilter = config.ScaleFilter ?? "area";
                    Console.WriteLine($"[ALERT] Config loaded: URL={StreamUrl}, Timeout={TimeoutSeconds}s");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ALERT] Failed to load config: {ex.Message}");
        }
    }

    private void SaveConfig()
    {
        try
        {
            var config = new AlertConfig
            {
                StreamUrl = StreamUrl,
                TimeoutSeconds = TimeoutSeconds,
                ScaleFilter = ScaleFilter
            };
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            FileHelper.AtomicWriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ALERT] Failed to save config: {ex.Message}");
        }
    }

    public void Dispose()
    {
        DismissAlert();
    }

    private class AlertConfig
    {
        public string? StreamUrl { get; set; }
        public int TimeoutSeconds { get; set; } = 30;
        public string? ScaleFilter { get; set; } = "area";
    }
}
