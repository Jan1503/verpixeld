using System.Diagnostics;
using System.Text.Json;
using CanvasManagement;
using SkiaSharp;
using verpixeld.Configuration;
using verpixeld.MediaPlayer;

namespace verpixeld.Services;

/// <summary>
///     Local USB camera service: streams video from a v4l2 device (USB webcam)
///     to a canvas on the LED matrix via FFmpeg.
///     Uses <see cref="FfmpegFrameStreamer"/> for the FFmpeg-to-canvas pipeline.
/// </summary>
public class LocalCameraService : IDisposable
{
    private readonly CanvasManager _canvasManager;
    private readonly string _configPath;

    // Configuration
    public string? VideoDevice { get; set; } // e.g. "/dev/video0"
    public int Fps { get; set; } = 15;
    public string ScaleFilter { get; set; } = "area";
    public string InputFormat { get; set; } = "mjpeg"; // mjpeg or yuyv422
    public string InputResolution { get; set; } = "640x480";
    public string ActiveEffect { get; set; } = "none"; // Visual effect applied to frames

    // State
    public bool IsStreaming { get; private set; }

    // Display dimensions
    private int _width;
    private int _height;

    // Internal streaming
    private readonly object _stateLock = new();
    private Canvas? _cameraCanvas;
    private FfmpegFrameStreamer? _streamer;

    // When false, the canvas is owned by the caller (e.g. a Studio content step) and must NOT be removed on stop.
    private bool _ownsCanvas = true;

    public LocalCameraService(CanvasManager canvasManager, int width, int height)
    {
        _canvasManager = canvasManager;
        _width = width;
        _height = height;

        _configPath = AppPaths.LocalCamConfig;
        LoadConfig();
        Console.WriteLine($"[LOCALCAM] Initialized for {width}x{height} display");
    }

    // ═══════════════════════════════════════════════════════════════
    // Start / Stop
    // ═══════════════════════════════════════════════════════════════

    public bool StartStream(string? canvasName = null)
    {
        lock (_stateLock)
        {
            if (IsStreaming) return true;

            if (string.IsNullOrEmpty(VideoDevice))
            {
                Console.WriteLine("[LOCALCAM] No video device configured");
                return false;
            }

            Console.WriteLine($"[LOCALCAM] Starting stream from {VideoDevice}");
            IsStreaming = true;
            _ownsCanvas = true;

            // Create a canvas (z-order 150)
            _cameraCanvas = _canvasManager.GetCanvas(0, 0, _width, _height, 150, canvasName ?? "LocalCamera");
            _cameraCanvas.Show();

            var ffmpegArgs = $"-hide_banner -loglevel warning " +
                             $"-f v4l2 -input_format {InputFormat} " +
                             $"-framerate {Fps} -video_size {InputResolution} " +
                             $"-i {VideoDevice} " +
                             $"-f rawvideo -pix_fmt {FfmpegRawVideo.PixFmt} " +
                             $"-vf \"scale={_width}:{_height}:flags={ScaleFilter}\" " +
                             $"-fps_mode cfr -an pipe:1";

            _streamer = new FfmpegFrameStreamer(_width, _height);

            // Wire up the frame effect processor
            var effect = ActiveEffect;
            if (!string.IsNullOrEmpty(effect) && effect != "none")
                _streamer.FrameProcessor = (rgb, w, h) => ApplyFrameEffect(rgb, w, h, effect);

            _streamer.Start(ffmpegArgs, _cameraCanvas, displayFps: 20, logPrefix: "[LOCALCAM]");

            return true;
        }
    }

    /// <summary>
    ///     Streams the USB camera into an EXISTING caller-owned canvas (e.g. a Studio content/rotation step),
    ///     scaling to that canvas's size. The canvas is NOT created or removed by this service.
    /// </summary>
    public bool StartStreamOnCanvas(Canvas canvas, string? device = null, string? effect = null)
    {
        lock (_stateLock)
        {
            if (IsStreaming) return false; // one camera stream at a time — caller stops first

            // Per-stream overrides (from a content step) take precedence over the configured defaults,
            // without persisting them to the global camera config.
            var dev = !string.IsNullOrWhiteSpace(device) ? device : VideoDevice;
            var fx = !string.IsNullOrWhiteSpace(effect) ? effect : ActiveEffect;
            if (string.IsNullOrEmpty(dev))
            {
                Console.WriteLine("[LOCALCAM] No video device configured");
                return false;
            }

            var w = canvas.Width;
            var h = canvas.Height;
            Console.WriteLine($"[LOCALCAM] Streaming {dev} into canvas {w}x{h} (effect={fx})");
            IsStreaming = true;
            _ownsCanvas = false;
            _cameraCanvas = canvas;

            var ffmpegArgs = $"-hide_banner -loglevel warning " +
                             $"-f v4l2 -input_format {InputFormat} " +
                             $"-framerate {Fps} -video_size {InputResolution} " +
                             $"-i {dev} " +
                             $"-f rawvideo -pix_fmt {FfmpegRawVideo.PixFmt} " +
                             $"-vf \"scale={w}:{h}:flags={ScaleFilter}\" " +
                             $"-fps_mode cfr -an pipe:1";

            _streamer = new FfmpegFrameStreamer(w, h);
            if (!string.IsNullOrEmpty(fx) && fx != "none")
                _streamer.FrameProcessor = (rgb, fw, fh) => ApplyFrameEffect(rgb, fw, fh, fx);

            _streamer.Start(ffmpegArgs, canvas, displayFps: 20, logPrefix: "[LOCALCAM]");
            return true;
        }
    }

    public void StopStream()
    {
        FfmpegFrameStreamer? streamer;
        Canvas? canvas;
        bool owns;

        lock (_stateLock)
        {
            if (!IsStreaming) return;

            Console.WriteLine("[LOCALCAM] Stopping stream");
            IsStreaming = false;

            streamer = _streamer;
            canvas = _cameraCanvas;
            owns = _ownsCanvas;

            _streamer = null;
            _cameraCanvas = null;
        }

        // Stop streamer (outside lock)
        streamer?.Stop("[LOCALCAM]");
        streamer?.Dispose();

        if (canvas != null)
            try
            {
                if (owns)
                {
                    // We created this canvas — clean it up entirely.
                    canvas.Clear();
                    canvas.Hide();
                    _canvasManager.RemoveCanvas(canvas);
                }
                else
                {
                    // Caller-owned (Studio canvas) — just clear the last frame, leave the canvas in place.
                    canvas.Clear();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LOCALCAM] Canvas cleanup error: {ex.Message}");
            }

        Console.WriteLine("[LOCALCAM] Stream stopped");
    }

    /// <summary>
    ///     Capture a single frame and return it as base64 PNG.
    ///     Useful for taking snapshots without continuous streaming.
    /// </summary>
    public async Task<string?> CaptureFrameAsync()
    {
        if (string.IsNullOrEmpty(VideoDevice))
            return null;

        try
        {
            var args = $"-f v4l2 -input_format {InputFormat} -video_size {InputResolution} " +
                       $"-i {VideoDevice} " +
                       $"-frames:v 1 -f rawvideo -pix_fmt {FfmpegRawVideo.PixFmt} " +
                       $"-vf \"scale={_width}:{_height}:flags={ScaleFilter}\" pipe:1";

            var psi = new ProcessStartInfo("ffmpeg", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            var frameSize = FfmpegRawVideo.FrameBytes(_width, _height);
            var buffer = new byte[frameSize];
            var stream = process.StandardOutput.BaseStream;
            var bytesRead = 0;

            while (bytesRead < frameSize)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(bytesRead, frameSize - bytesRead));
                if (read == 0) break;
                bytesRead += read;
            }

            await process.WaitForExitAsync();

            if (bytesRead < frameSize) return null;

            using var bitmap = new SKBitmap(_width, _height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            FfmpegRawVideo.CopyToBitmap(bitmap, buffer);

            using var img = SKImage.FromBitmap(bitmap);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            return Convert.ToBase64String(data.ToArray());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LOCALCAM] Capture frame error: {ex.Message}");
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Frame Visual Effects (applied to raw RGB24 data in-place)
    // ═══════════════════════════════════════════════════════════════

    private static void ApplyFrameEffect(byte[] rgb, int w, int h, string effect)
    {
        switch (effect)
        {
            case "edge":       ApplyEdgeDetection(rgb, w, h); break;
            case "invert":     ApplyInvert(rgb, w, h); break;
            case "sepia":      ApplySepia(rgb, w, h); break;
            case "nightvision": ApplyNightVision(rgb, w, h); break;
            case "thermal":    ApplyThermal(rgb, w, h); break;
            case "posterize":  ApplyPosterize(rgb, w, h); break;
            case "pixelate":   ApplyPixelate(rgb, w, h); break;
            case "rgbshift":   ApplyRgbShift(rgb, w, h); break;
            case "emboss":     ApplyEmboss(rgb, w, h); break;
            case "blur":       ApplyBlur(rgb, w, h); break;
        }
    }

    private static void ApplyInvert(byte[] rgb, int w, int h)
    {
        for (var i = 0; i < w * h * 3; i++)
            rgb[i] = (byte)(255 - rgb[i]);
    }

    private static void ApplySepia(byte[] rgb, int w, int h)
    {
        for (var i = 0; i < w * h * 3; i += 3)
        {
            int r = rgb[i], g = rgb[i + 1], b = rgb[i + 2];
            rgb[i]     = (byte)Math.Min(255, (int)(r * 0.393 + g * 0.769 + b * 0.189));
            rgb[i + 1] = (byte)Math.Min(255, (int)(r * 0.349 + g * 0.686 + b * 0.168));
            rgb[i + 2] = (byte)Math.Min(255, (int)(r * 0.272 + g * 0.534 + b * 0.131));
        }
    }

    private static readonly Random _effectRng = new();

    private static void ApplyNightVision(byte[] rgb, int w, int h)
    {
        for (var i = 0; i < w * h * 3; i += 3)
        {
            var lum = rgb[i] * 0.299 + rgb[i + 1] * 0.587 + rgb[i + 2] * 0.114;
            var noise = (_effectRng.NextDouble() - 0.5) * 20;
            rgb[i]     = (byte)Math.Clamp((int)(lum * 0.2 + noise), 0, 255);
            rgb[i + 1] = (byte)Math.Clamp((int)(lum * 1.4 + noise), 0, 255);
            rgb[i + 2] = (byte)Math.Clamp((int)(lum * 0.2 + noise), 0, 255);
        }
    }

    private static void ApplyThermal(byte[] rgb, int w, int h)
    {
        for (var i = 0; i < w * h * 3; i += 3)
        {
            var temp = (rgb[i] * 0.299 + rgb[i + 1] * 0.587 + rgb[i + 2] * 0.114) / 255.0;
            if (temp < 0.2)
            { rgb[i] = 0; rgb[i + 1] = 0; rgb[i + 2] = (byte)(temp * 5 * 200); }
            else if (temp < 0.4)
            { var t = (temp - 0.2) * 5; rgb[i] = (byte)(t * 180); rgb[i + 1] = 0; rgb[i + 2] = (byte)(200 - t * 100); }
            else if (temp < 0.6)
            { var t = (temp - 0.4) * 5; rgb[i] = (byte)(180 + t * 75); rgb[i + 1] = (byte)(t * 100); rgb[i + 2] = (byte)(100 - t * 100); }
            else if (temp < 0.8)
            { var t = (temp - 0.6) * 5; rgb[i] = 255; rgb[i + 1] = (byte)(100 + t * 155); rgb[i + 2] = 0; }
            else
            { var t = (temp - 0.8) * 5; rgb[i] = 255; rgb[i + 1] = 255; rgb[i + 2] = (byte)(t * 255); }
        }
    }

    private static void ApplyPosterize(byte[] rgb, int w, int h)
    {
        const int levels = 4;
        const double step = 255.0 / (levels - 1);
        for (var i = 0; i < w * h * 3; i++)
            rgb[i] = (byte)(Math.Round(rgb[i] / step) * step);
    }

    private static void ApplyPixelate(byte[] rgb, int w, int h)
    {
        var blockSize = Math.Max(2, Math.Min(w, h) / 16);
        for (var by = 0; by < h; by += blockSize)
        {
            for (var bx = 0; bx < w; bx += blockSize)
            {
                int rSum = 0, gSum = 0, bSum = 0, count = 0;
                var yEnd = Math.Min(by + blockSize, h);
                var xEnd = Math.Min(bx + blockSize, w);
                for (var y = by; y < yEnd; y++)
                    for (var x = bx; x < xEnd; x++)
                    {
                        var idx = (y * w + x) * 3;
                        rSum += rgb[idx]; gSum += rgb[idx + 1]; bSum += rgb[idx + 2]; count++;
                    }
                byte rAvg = (byte)(rSum / count), gAvg = (byte)(gSum / count), bAvg = (byte)(bSum / count);
                for (var y = by; y < yEnd; y++)
                    for (var x = bx; x < xEnd; x++)
                    {
                        var idx = (y * w + x) * 3;
                        rgb[idx] = rAvg; rgb[idx + 1] = gAvg; rgb[idx + 2] = bAvg;
                    }
            }
        }
    }

    private static void ApplyRgbShift(byte[] rgb, int w, int h)
    {
        var shift = Math.Max(1, w / 40);
        var src = new byte[rgb.Length];
        Buffer.BlockCopy(rgb, 0, src, 0, rgb.Length);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var idx = (y * w + x) * 3;
                rgb[idx]     = src[(y * w + Math.Max(0, x - shift)) * 3];
                rgb[idx + 1] = src[idx + 1];
                rgb[idx + 2] = src[(y * w + Math.Min(w - 1, x + shift)) * 3 + 2];
            }
    }

    private static void ApplyEdgeDetection(byte[] rgb, int w, int h)
    {
        var src = new byte[rgb.Length];
        Buffer.BlockCopy(rgb, 0, src, 0, rgb.Length);
        for (var y = 1; y < h - 1; y++)
            for (var x = 1; x < w - 1; x++)
            {
                int gx = 0, gy = 0;
                for (var c = 0; c < 3; c++)
                {
                    var tl = src[((y - 1) * w + x - 1) * 3 + c]; var t = src[((y - 1) * w + x) * 3 + c]; var tr = src[((y - 1) * w + x + 1) * 3 + c];
                    var l = src[(y * w + x - 1) * 3 + c]; var r = src[(y * w + x + 1) * 3 + c];
                    var bl = src[((y + 1) * w + x - 1) * 3 + c]; var b = src[((y + 1) * w + x) * 3 + c]; var br = src[((y + 1) * w + x + 1) * 3 + c];
                    gx += Math.Abs(-tl + tr - 2 * l + 2 * r - bl + br);
                    gy += Math.Abs(-tl - 2 * t - tr + bl + 2 * b + br);
                }
                var mag = (byte)Math.Min(255, (gx + gy) / 3);
                var idx = (y * w + x) * 3;
                rgb[idx] = rgb[idx + 1] = rgb[idx + 2] = mag;
            }
    }

    private static void ApplyEmboss(byte[] rgb, int w, int h)
    {
        var src = new byte[rgb.Length];
        Buffer.BlockCopy(rgb, 0, src, 0, rgb.Length);
        for (var y = 1; y < h - 1; y++)
            for (var x = 1; x < w - 1; x++)
            {
                var idx = (y * w + x) * 3;
                for (var c = 0; c < 3; c++)
                {
                    var val = 128
                        - 2 * src[((y - 1) * w + x - 1) * 3 + c] - src[((y - 1) * w + x) * 3 + c]
                        - src[(y * w + x - 1) * 3 + c] + src[(y * w + x) * 3 + c] + src[(y * w + x + 1) * 3 + c]
                        + src[((y + 1) * w + x) * 3 + c] + 2 * src[((y + 1) * w + x + 1) * 3 + c];
                    rgb[idx + c] = (byte)Math.Clamp(val, 0, 255);
                }
            }
    }

    private static void ApplyBlur(byte[] rgb, int w, int h)
    {
        var src = new byte[rgb.Length];
        Buffer.BlockCopy(rgb, 0, src, 0, rgb.Length);
        for (var y = 1; y < h - 1; y++)
            for (var x = 1; x < w - 1; x++)
            {
                var idx = (y * w + x) * 3;
                for (var c = 0; c < 3; c++)
                {
                    var sum = 0;
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                            sum += src[((y + dy) * w + (x + dx)) * 3 + c];
                    rgb[idx + c] = (byte)(sum / 9);
                }
            }
    }


    // ═══════════════════════════════════════════════════════════════
    // Device Discovery
    // ═══════════════════════════════════════════════════════════════

    public static List<DeviceInfo> ListVideoDevices()
    {
        var devices = new List<DeviceInfo>();
        try
        {
            var psi = new ProcessStartInfo("bash", "-c \"ls /dev/video* 2>/dev/null\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return devices;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var device = line.Trim();
                if (!device.StartsWith("/dev/video")) continue;

                var name = GetDeviceName(device);
                devices.Add(new DeviceInfo { Path = device, Name = name });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LOCALCAM] Device discovery error: {ex.Message}");
        }

        return devices;
    }

    public static List<DeviceInfo> ListAudioDevices()
    {
        var devices = new List<DeviceInfo>();

        try
        {
            var psi = new ProcessStartInfo("pactl", "list sources short")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(3000);

                Console.WriteLine($"[LOCALCAM] pactl sources:\n{output.TrimEnd()}");

                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;

                    var sourceName = parts[1].Trim();
                    if (sourceName.Contains(".monitor")) continue;
                    if (!sourceName.StartsWith("alsa_input.")) continue;

                    var friendlyName = sourceName
                        .Replace("alsa_input.usb-", "")
                        .Replace("alsa_input.", "");

                    var dotIdx = friendlyName.LastIndexOf('.');
                    if (dotIdx > 0) friendlyName = friendlyName[..dotIdx];

                    friendlyName = System.Text.RegularExpressions.Regex.Replace(
                        friendlyName, @"-\d+$", "");
                    friendlyName = friendlyName.Replace('_', ' ').Replace('-', ' ').Trim();

                    devices.Add(new DeviceInfo { Path = sourceName, Name = friendlyName });
                }

                if (devices.Count > 0)
                {
                    Console.WriteLine($"[LOCALCAM] Found {devices.Count} PulseAudio input source(s)");
                    return devices;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LOCALCAM] pactl failed: {ex.Message}");
        }

        // Fallback: read /proc/asound/cards
        try
        {
            const string cardsFile = "/proc/asound/cards";
            if (File.Exists(cardsFile))
            {
                var lines = File.ReadAllLines(cardsFile);
                foreach (var line in lines)
                {
                    var match = System.Text.RegularExpressions.Regex.Match(
                        line, @"^\s*(\d+)\s+\[(\w+)\s*\]\s*:\s*\S+\s*-\s*(.+)$");
                    if (!match.Success) continue;

                    var cardNum = match.Groups[1].Value;
                    var shortName = match.Groups[2].Value.Trim();
                    var longName = match.Groups[3].Value.Trim();

                    devices.Add(new DeviceInfo
                    {
                        Path = $"plughw:{cardNum},0",
                        Name = $"{longName} [{shortName}]"
                    });
                }

                if (devices.Count > 0)
                {
                    Console.WriteLine($"[LOCALCAM] Found {devices.Count} ALSA device(s) via /proc/asound/cards");
                    return devices;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LOCALCAM] /proc/asound/cards error: {ex.Message}");
        }

        Console.WriteLine($"[LOCALCAM] Found {devices.Count} audio device(s) total");
        return devices;
    }

    private static string GetDeviceName(string devicePath)
    {
        try
        {
            var psi = new ProcessStartInfo("bash", $"-c \"v4l2-ctl --device={devicePath} --info 2>/dev/null | head -2\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return devicePath;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(1000);

            foreach (var line in output.Split('\n'))
            {
                if (line.Contains("Card type"))
                    return line.Split(':').Last().Trim();
            }
        }
        catch { }

        return devicePath;
    }

    // ═══════════════════════════════════════════════════════════════
    // Configuration Persistence
    // ═══════════════════════════════════════════════════════════════

    public void Configure(string? videoDevice, int? fps, string? scaleFilter,
        string? inputFormat, string? inputResolution, string? activeEffect = null)
    {
        if (videoDevice != null) VideoDevice = videoDevice;
        if (fps.HasValue) Fps = Math.Clamp(fps.Value, 1, 30);
        if (scaleFilter != null) ScaleFilter = scaleFilter;
        if (inputFormat != null) InputFormat = inputFormat;
        if (inputResolution != null) InputResolution = inputResolution;
        if (activeEffect != null) ActiveEffect = activeEffect;

        SaveConfig();
        Console.WriteLine($"[LOCALCAM] Configured: Device={VideoDevice}, FPS={Fps}, Format={InputFormat}, Effect={ActiveEffect}");
    }

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(_configPath)) return;
            var json = File.ReadAllText(_configPath);
            var config = JsonSerializer.Deserialize<LocalCamConfig>(json);
            if (config == null) return;

            VideoDevice = config.VideoDevice;
            Fps = config.Fps > 0 ? config.Fps : 15;
            ScaleFilter = config.ScaleFilter ?? "area";
            InputFormat = config.InputFormat ?? "mjpeg";
            InputResolution = config.InputResolution ?? "640x480";
            ActiveEffect = config.ActiveEffect ?? "none";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LOCALCAM] Failed to load config: {ex.Message}");
        }
    }

    private void SaveConfig()
    {
        try
        {
            var config = new LocalCamConfig
            {
                VideoDevice = VideoDevice,
                Fps = Fps,
                ScaleFilter = ScaleFilter,
                InputFormat = InputFormat,
                InputResolution = InputResolution,
                ActiveEffect = ActiveEffect
            };
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            FileHelper.AtomicWriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LOCALCAM] Failed to save config: {ex.Message}");
        }
    }

    public void Dispose()
    {
        StopStream();
        GC.SuppressFinalize(this);
    }

    // ═══════════════════════════════════════════════════════════════
    // Models
    // ═══════════════════════════════════════════════════════════════

    private class LocalCamConfig
    {
        public string? VideoDevice { get; set; }
        public int Fps { get; set; }
        public string? ScaleFilter { get; set; }
        public string? InputFormat { get; set; }
        public string? InputResolution { get; set; }
        public string? ActiveEffect { get; set; }
    }
}

public class DeviceInfo
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
}
