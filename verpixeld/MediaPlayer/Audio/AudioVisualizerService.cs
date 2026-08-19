using System.Diagnostics;
using System.Numerics;
using CanvasManagement;
using SkiaSharp;
using verpixeld.Services;

namespace verpixeld.MediaPlayer.Audio;

/// <summary>
///     Service for real-time audio visualization using FFT analysis.
///     Captures system audio via PulseAudio monitor and renders visualizations to a canvas.
/// </summary>
public class AudioVisualizerService : IAudioVisualizerService
{
    public enum ColorScheme
    {
        Rainbow,
        Fire,
        Ocean,
        Mono,
        Gradient
    }

    public enum VisualizationMode
    {
        SpectrumBars,
        SpectrumWave,
        MirrorBars,
        CircularSpectrum,
        Waveform
    }

    // Audio capture settings
    private const int SampleRate = 44100;
    private const int Channels = 1; // Mono for simplicity
    private const int BitsPerSample = 16;
    private const int FFTSize = 1024; // Power of 2 for FFT
    private const int NumBands = 32; // Number of frequency bands to display
    private const int TargetFPS = 30;

    // Audio buffer
    private readonly short[] _audioBuffer = new short[FFTSize];
    private readonly double[] _bandValues = new double[NumBands];
    private readonly object _bufferLock = new();
    private readonly double[] _fftMagnitudes = new double[FFTSize / 2];
    private readonly double[] _smoothedBands = new double[NumBands];
    private readonly double[] _hammingWindow = new double[FFTSize];
    private readonly Complex[] _fftSamples = new Complex[FFTSize];
    private int _bufferPosition;
    private volatile bool _canvasDisposed;
    private Task? _captureTask;
    private CancellationTokenSource? _cts;

    // State
    private Process? _parecProcess;
    private Task? _renderTask;
    private Canvas? _targetCanvas;
    private SKBitmap? _frameBitmap;

    // Pre-computed FFT twiddle factors
    private Complex[]? _twiddleFactors;

    public AudioVisualizerService()
    {
        PrecomputeTwiddleFactors();
        for (var i = 0; i < FFTSize; i++)
            _hammingWindow[i] = 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (FFTSize - 1));
    }

    // Visualization settings
    public VisualizationMode Mode { get; set; } = VisualizationMode.SpectrumBars;
    public double Sensitivity { get; set; } = 1.0;
    public ColorScheme ColorMode { get; set; } = ColorScheme.Rainbow;
    public bool MirrorBars { get; set; } = false;
    public double SmoothingFactor { get; set; } = 0.7; // 0 = no smoothing, 1 = max smoothing

    public bool IsRunning { get; private set; }



    public string? TargetCanvasName { get; private set; }

    public void Dispose()
    {
        StopAsync().Wait();
        _parecProcess?.Dispose();
        _frameBitmap?.Dispose();
        _frameBitmap = null;
    }

    /// <summary>
    ///     Notify that the target canvas has been removed/disposed
    /// </summary>
    public void NotifyCanvasRemoved(string canvasName)
    {
        if (TargetCanvasName == canvasName && IsRunning)
        {
            Console.WriteLine($"[VISUALIZER] Canvas '{canvasName}' removed - stopping visualizer");
            _canvasDisposed = true;
            _cts?.Cancel();
        }
    }

    /// <summary>
    ///     Start the visualizer on the specified canvas
    /// </summary>
    public async Task<bool> StartAsync(Canvas canvas, string canvasName)
    {
        if (IsRunning)
        {
            Console.WriteLine("[VISUALIZER] Already running, stopping first...");
            await StopAsync();
        }

        _canvasDisposed = false;
        _targetCanvas = canvas;
        TargetCanvasName = canvasName;

        // Find the monitor source for the default sink
        var monitorSource = await GetMonitorSourceAsync();
        if (string.IsNullOrEmpty(monitorSource))
        {
            Console.WriteLine("[VISUALIZER] Could not find PulseAudio monitor source");
            return false;
        }

        Console.WriteLine($"[VISUALIZER] Starting with monitor source: {monitorSource}");
        Console.WriteLine($"[VISUALIZER] Target canvas: {canvasName} ({canvas.Width}x{canvas.Height})");

        _cts = new CancellationTokenSource();

        // Start audio capture
        _captureTask = Task.Run(() => CaptureAudioAsync(monitorSource, _cts.Token));

        // Start render loop
        _renderTask = Task.Run(() => RenderLoopAsync(_cts.Token));

        IsRunning = true;
        Console.WriteLine("[VISUALIZER] Started");

        return true;
    }

    /// <summary>
    ///     Stop the visualizer
    /// </summary>
    public async Task StopAsync()
    {
        if (!IsRunning) return;

        Console.WriteLine("[VISUALIZER] Stopping...");

        _cts?.Cancel();

        // Kill parec process
        if (_parecProcess != null && !_parecProcess.HasExited)
        {
            try
            {
                _parecProcess.Kill();
            }
            catch
            {
            }

            _parecProcess.Dispose();
            _parecProcess = null;
        }

        // Wait for tasks
        if (_captureTask != null)
            try
            {
                await _captureTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }

        if (_renderTask != null)
            try
            {
                await _renderTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }

        // Clear the canvas only if not disposed
        if (_targetCanvas != null && !_canvasDisposed)
            try
            {
                _targetCanvas.Clear();
            }
            catch (ObjectDisposedException)
            {
                // Canvas already disposed, ignore
            }

        IsRunning = false;
        TargetCanvasName = null;
        _targetCanvas = null;
        _canvasDisposed = false;
        _cts?.Dispose();
        _cts = null;

        Console.WriteLine("[VISUALIZER] Stopped");
    }

    /// <summary>
    ///     Get the PulseAudio monitor source for the default sink
    /// </summary>
    private async Task<string?> GetMonitorSourceAsync()
    {
        try
        {
            // Get the default sink name
            var psi = new ProcessStartInfo("pactl", "get-default-sink")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            PulseAudioHelper.ApplyPulseEnv(psi);

            using var proc = Process.Start(psi);
            if (proc == null) return null;

            var defaultSink = (await proc.StandardOutput.ReadToEndAsync()).Trim();
            await proc.WaitForExitAsync();

            if (string.IsNullOrEmpty(defaultSink))
            {
                Console.WriteLine("[VISUALIZER] No default sink found");
                return null;
            }

            // The monitor source is typically the sink name + ".monitor"
            var monitorSource = $"{defaultSink}.monitor";
            Console.WriteLine($"[VISUALIZER] Default sink: {defaultSink}, monitor: {monitorSource}");

            return monitorSource;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VISUALIZER] Error getting monitor source: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    ///     Capture audio from PulseAudio monitor source
    /// </summary>
    private async Task CaptureAudioAsync(string monitorSource, CancellationToken ct)
    {
        try
        {
            // Use parec to capture audio
            // parec --rate=44100 --channels=1 --format=s16le --device=<monitor>
            var psi = new ProcessStartInfo("parec",
                $"--rate={SampleRate} --channels={Channels} --format=s16le --latency-msec=30 --device={monitorSource}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            PulseAudioHelper.ApplyPulseEnv(psi, 30);

            _parecProcess = Process.Start(psi);
            if (_parecProcess == null)
            {
                Console.WriteLine("[VISUALIZER] Failed to start parec");
                return;
            }

            Console.WriteLine($"[VISUALIZER] parec started (PID: {_parecProcess.Id})");

            var stream = _parecProcess.StandardOutput.BaseStream;
            var buffer = new byte[FFTSize * 2]; // 16-bit = 2 bytes per sample

            while (!ct.IsCancellationRequested && !_parecProcess.HasExited)
                try
                {
                    var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                    if (bytesRead == 0) continue;

                    // Convert bytes to shorts and fill buffer
                    lock (_bufferLock)
                    {
                        for (var i = 0; i < bytesRead - 1; i += 2)
                        {
                            var sample = (short)(buffer[i] | (buffer[i + 1] << 8));
                            _audioBuffer[_bufferPosition] = sample;
                            _bufferPosition = (_bufferPosition + 1) % FFTSize;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[VISUALIZER] Capture error: {ex.Message}");
                    await Task.Delay(100, ct);
                }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VISUALIZER] Capture task error: {ex.Message}");
        }
    }

    /// <summary>
    ///     Main render loop
    /// </summary>
    private async Task RenderLoopAsync(CancellationToken ct)
    {
        var frameTime = TimeSpan.FromMilliseconds(1000.0 / TargetFPS);
        var stopwatch = Stopwatch.StartNew();

        while (!ct.IsCancellationRequested && !_canvasDisposed)
        {
            var frameStart = stopwatch.ElapsedMilliseconds;

            try
            {
                // Check if canvas was disposed
                if (_canvasDisposed)
                {
                    Console.WriteLine("[VISUALIZER] Canvas disposed - exiting render loop");
                    break;
                }

                // Get canvas
                if (_targetCanvas == null)
                {
                    await Task.Delay(100, ct);
                    continue;
                }

                var canvas = _targetCanvas;

                // Perform FFT analysis
                AnalyzeAudio();

                // Render visualization based on mode
                switch (Mode)
                {
                    case VisualizationMode.SpectrumBars:
                        RenderSpectrumBars(canvas);
                        break;
                    case VisualizationMode.MirrorBars:
                        RenderMirrorBars(canvas);
                        break;
                    case VisualizationMode.SpectrumWave:
                        RenderSpectrumWave(canvas);
                        break;
                    case VisualizationMode.CircularSpectrum:
                        RenderCircularSpectrum(canvas);
                        break;
                    case VisualizationMode.Waveform:
                        RenderWaveform(canvas);
                        break;
                }

                // Check again after render
                if (_canvasDisposed) break;

                // Frame timing
                var elapsed = stopwatch.ElapsedMilliseconds - frameStart;
                var sleepTime = (int)(frameTime.TotalMilliseconds - elapsed);
                if (sleepTime > 0) await Task.Delay(sleepTime, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                Console.WriteLine("[VISUALIZER] Canvas disposed during render - stopping");
                _canvasDisposed = true;
                break;
            }
            catch (Exception ex)
            {
                // Don't spam logs if canvas is disposed
                if (!_canvasDisposed) Console.WriteLine($"[VISUALIZER] Render error: {ex.Message}");
                await Task.Delay(100, ct);
            }
        }
    }

    /// <summary>
    ///     Perform FFT analysis on the audio buffer
    /// </summary>
    private void AnalyzeAudio()
    {
        // Copy buffer and apply window function
        lock (_bufferLock)
        {
            for (var i = 0; i < FFTSize; i++)
            {
                var normalizedSample = _audioBuffer[(i + _bufferPosition) % FFTSize] / 32768.0;
                _fftSamples[i] = new Complex(normalizedSample * _hammingWindow[i], 0);
            }
        }

        // Perform FFT
        FFT(_fftSamples);

        // Calculate magnitudes
        for (var i = 0; i < FFTSize / 2; i++) _fftMagnitudes[i] = _fftSamples[i].Magnitude;

        // Map to frequency bands (logarithmic scale for better visualization)
        MapToFrequencyBands();
    }

    /// <summary>
    ///     Map FFT bins to frequency bands using logarithmic scale
    /// </summary>
    private void MapToFrequencyBands()
    {
        // Use logarithmic frequency scale for more musical distribution
        // Lower bands cover fewer bins (bass), higher bands cover more bins (treble)

        double minFreq = 20; // 20 Hz
        var maxFreq = SampleRate / 2.0; // Nyquist frequency

        for (var band = 0; band < NumBands; band++)
        {
            // Logarithmic distribution
            var freqLow = minFreq * Math.Pow(maxFreq / minFreq, (double)band / NumBands);
            var freqHigh = minFreq * Math.Pow(maxFreq / minFreq, (double)(band + 1) / NumBands);

            // Convert to bin indices
            var binLow = (int)(freqLow * FFTSize / SampleRate);
            var binHigh = (int)(freqHigh * FFTSize / SampleRate);

            binLow = Math.Clamp(binLow, 0, FFTSize / 2 - 1);
            binHigh = Math.Clamp(binHigh, binLow + 1, FFTSize / 2);

            // Average the magnitudes in this range
            double sum = 0;
            for (var i = binLow; i < binHigh; i++) sum += _fftMagnitudes[i];
            var avg = sum / (binHigh - binLow);

            // Apply sensitivity
            _bandValues[band] = avg * Sensitivity * 2.0;

            // Apply smoothing (exponential moving average)
            _smoothedBands[band] = _smoothedBands[band] * SmoothingFactor +
                                   _bandValues[band] * (1 - SmoothingFactor);
        }
    }

    /// <summary>
    ///     Cooley-Tukey FFT algorithm (in-place)
    /// </summary>
    private void FFT(Complex[] samples)
    {
        var n = samples.Length;
        if (n <= 1) return;

        // Bit-reversal permutation
        var bits = (int)Math.Log2(n);
        for (var i = 0; i < n; i++)
        {
            var j = BitReverse(i, bits);
            if (j > i) (samples[i], samples[j]) = (samples[j], samples[i]);
        }

        // Cooley-Tukey iterative FFT
        for (var len = 2; len <= n; len *= 2)
        {
            var angle = -2 * Math.PI / len;
            var wlen = new Complex(Math.Cos(angle), Math.Sin(angle));

            for (var i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (var j = 0; j < len / 2; j++)
                {
                    var u = samples[i + j];
                    var v = samples[i + j + len / 2] * w;
                    samples[i + j] = u + v;
                    samples[i + j + len / 2] = u - v;
                    w *= wlen;
                }
            }
        }
    }

    private static int BitReverse(int x, int bits)
    {
        var result = 0;
        for (var i = 0; i < bits; i++)
        {
            result = (result << 1) | (x & 1);
            x >>= 1;
        }

        return result;
    }

    private void PrecomputeTwiddleFactors()
    {
        _twiddleFactors = new Complex[FFTSize / 2];
        for (var i = 0; i < FFTSize / 2; i++)
        {
            var angle = -2 * Math.PI * i / FFTSize;
            _twiddleFactors[i] = new Complex(Math.Cos(angle), Math.Sin(angle));
        }
    }

    // ========================================================================
    // VISUALIZATION RENDERERS
    // ========================================================================

    /// <summary>
    ///     Draw the bitmap to the canvas
    /// </summary>
    private void DrawToCanvas(Canvas canvas, SKBitmap bitmap)
    {
        if (_canvasDisposed || canvas == null) return;

        try
        {
            canvas.DrawBitmap(bitmap, 0, 0);
        }
        catch (ObjectDisposedException)
        {
            _canvasDisposed = true;
            _cts?.Cancel();
        }
    }

    private SKBitmap GetFrameBitmap(int width, int height)
    {
        if (_frameBitmap == null || _frameBitmap.Width != width || _frameBitmap.Height != height)
        {
            _frameBitmap?.Dispose();
            _frameBitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        }

        return _frameBitmap;
    }

    /// <summary>
    ///     Set a pixel in the bitmap (RGBA8888 format)
    /// </summary>
    private static unsafe void SetPixel(uint* pixels, int width, int x, int y, byte r, byte g, byte b)
    {
        if (x >= 0 && x < width && y >= 0)
            // RGBA8888 format: 0xAABBGGRR (little-endian)
            pixels[y * width + x] = 0xFF000000 | ((uint)b << 16) | ((uint)g << 8) | r;
    }

    /// <summary>
    ///     Render spectrum as vertical bars
    /// </summary>
    private void RenderSpectrumBars(Canvas canvas)
    {
        var width = canvas.Width;
        var height = canvas.Height;

        var bitmap = GetFrameBitmap(width, height);

        unsafe
        {
            var pixels = (uint*)bitmap.GetPixels().ToPointer();

            // Clear to black
            for (var i = 0; i < width * height; i++)
                pixels[i] = 0xFF000000;

            var barWidth = Math.Max(1, width / NumBands);
            var gap = Math.Max(0, (width - barWidth * NumBands) / (NumBands + 1));

            for (var i = 0; i < NumBands; i++)
            {
                var value = Math.Min(1.0, _smoothedBands[i]);
                var barHeight = (int)(value * height);
                var x = gap + i * (barWidth + gap);

                var color = GetColor(i, NumBands, value);

                for (var y = 0; y < barHeight && y < height; y++)
                for (var bx = 0; bx < barWidth && x + bx < width; bx++)
                    SetPixel(pixels, width, x + bx, height - 1 - y, color.r, color.g, color.b);
            }
        }

        DrawToCanvas(canvas, bitmap);
    }

    /// <summary>
    ///     Render spectrum bars mirrored from center
    /// </summary>
    private void RenderMirrorBars(Canvas canvas)
    {
        var width = canvas.Width;
        var height = canvas.Height;
        var centerY = height / 2;

        var bitmap = GetFrameBitmap(width, height);

        unsafe
        {
            var pixels = (uint*)bitmap.GetPixels().ToPointer();

            for (var i = 0; i < width * height; i++)
                pixels[i] = 0xFF000000;

            var barWidth = Math.Max(1, width / NumBands);
            var gap = Math.Max(0, (width - barWidth * NumBands) / (NumBands + 1));

            for (var i = 0; i < NumBands; i++)
            {
                var value = Math.Min(1.0, _smoothedBands[i]);
                var barHeight = (int)(value * centerY);
                var x = gap + i * (barWidth + gap);

                var color = GetColor(i, NumBands, value);

                for (var y = 0; y < barHeight; y++)
                for (var bx = 0; bx < barWidth && x + bx < width; bx++)
                {
                    if (centerY - 1 - y >= 0)
                        SetPixel(pixels, width, x + bx, centerY - 1 - y, color.r, color.g, color.b);
                    if (centerY + y < height)
                        SetPixel(pixels, width, x + bx, centerY + y, color.r, color.g, color.b);
                }
            }
        }

        DrawToCanvas(canvas, bitmap);
    }

    /// <summary>
    ///     Render spectrum as a smooth wave
    /// </summary>
    private void RenderSpectrumWave(Canvas canvas)
    {
        var width = canvas.Width;
        var height = canvas.Height;

        var bitmap = GetFrameBitmap(width, height);

        var points = new int[width];

        for (var x = 0; x < width; x++)
        {
            var bandPos = (double)x / width * (NumBands - 1);
            var bandLow = (int)bandPos;
            var bandHigh = Math.Min(bandLow + 1, NumBands - 1);
            var frac = bandPos - bandLow;

            var value = _smoothedBands[bandLow] * (1 - frac) + _smoothedBands[bandHigh] * frac;
            value = Math.Min(1.0, value);
            points[x] = (int)(value * (height - 1));
        }

        unsafe
        {
            var pixels = (uint*)bitmap.GetPixels().ToPointer();

            for (var i = 0; i < width * height; i++)
                pixels[i] = 0xFF000000;

            for (var x = 0; x < width; x++)
            {
                var color = GetColor(x, width, points[x] / (double)height);
                for (var y = 0; y <= points[x] && y < height; y++)
                    SetPixel(pixels, width, x, height - 1 - y, color.r, color.g, color.b);
            }
        }

        DrawToCanvas(canvas, bitmap);
    }

    /// <summary>
    ///     Render spectrum in a circular pattern
    /// </summary>
    private void RenderCircularSpectrum(Canvas canvas)
    {
        var width = canvas.Width;
        var height = canvas.Height;
        var centerX = width / 2;
        var centerY = height / 2;
        var maxRadius = Math.Min(centerX, centerY) - 2;
        var minRadius = maxRadius / 3;

        var bitmap = GetFrameBitmap(width, height);

        unsafe
        {
            var pixels = (uint*)bitmap.GetPixels().ToPointer();

            for (var i = 0; i < width * height; i++)
                pixels[i] = 0xFF000000;

            for (var i = 0; i < NumBands; i++)
            {
                var angle1 = 2 * Math.PI * i / NumBands - Math.PI / 2;
                var angle2 = 2 * Math.PI * (i + 1) / NumBands - Math.PI / 2;
                var value = Math.Min(1.0, _smoothedBands[i]);
                var outerRadius = minRadius + (int)(value * (maxRadius - minRadius));

                var color = GetColor(i, NumBands, value);

                var steps = Math.Max(3, (int)((angle2 - angle1) * outerRadius));
                for (var s = 0; s < steps; s++)
                {
                    var angle = angle1 + (angle2 - angle1) * s / steps;
                    for (var r = minRadius; r <= outerRadius; r++)
                    {
                        var x = centerX + (int)(r * Math.Cos(angle));
                        var y = centerY + (int)(r * Math.Sin(angle));
                        if (x >= 0 && x < width && y >= 0 && y < height)
                            SetPixel(pixels, width, x, y, color.r, color.g, color.b);
                    }
                }
            }
        }

        DrawToCanvas(canvas, bitmap);
    }

    /// <summary>
    ///     Render raw waveform (oscilloscope style)
    /// </summary>
    private void RenderWaveform(Canvas canvas)
    {
        var width = canvas.Width;
        var height = canvas.Height;
        var centerY = height / 2;

        var bitmap = GetFrameBitmap(width, height);

        unsafe
        {
            var pixels = (uint*)bitmap.GetPixels().ToPointer();

            for (var i = 0; i < width * height; i++)
                pixels[i] = 0xFF000000;

            lock (_bufferLock)
            {
                for (var x = 0; x < width; x++)
                {
                    var sampleIndex = (int)((double)x / width * FFTSize);
                    sampleIndex = (_bufferPosition + sampleIndex) % FFTSize;

                    var normalized = _audioBuffer[sampleIndex] / 32768.0;
                    var y = centerY + (int)(normalized * centerY * Sensitivity);
                    y = Math.Clamp(y, 0, height - 1);

                    var y1 = Math.Min(centerY, y);
                    var y2 = Math.Max(centerY, y);

                    var color = GetColor(x, width, Math.Abs(normalized));
                    for (var py = y1; py <= y2; py++) SetPixel(pixels, width, x, py, color.r, color.g, color.b);
                }
            }
        }

        DrawToCanvas(canvas, bitmap);
    }

    /// <summary>
    ///     Get color based on scheme, position, and intensity
    /// </summary>
    private (byte r, byte g, byte b) GetColor(int index, int total, double intensity)
    {
        var t = (double)index / total;
        intensity = Math.Clamp(intensity, 0, 1);

        switch (ColorMode)
        {
            case ColorScheme.Rainbow:
                return HsvToRgb(t * 360, 1.0, 0.3 + intensity * 0.7);

            case ColorScheme.Fire:
                // Red -> Orange -> Yellow
                if (intensity < 0.5)
                    return ((byte)(255 * intensity * 2), 0, 0);
                return (255, (byte)(255 * (intensity - 0.5) * 2), 0);

            case ColorScheme.Ocean:
                // Deep blue -> Cyan -> White
                return ((byte)(intensity * 100), (byte)(100 + intensity * 155), (byte)(150 + intensity * 105));

            case ColorScheme.Mono:
                // Green monochrome (classic)
                var v = (byte)(intensity * 255);
                return (0, v, 0);

            case ColorScheme.Gradient:
                // Purple to Cyan gradient based on position, intensity affects brightness
                return HsvToRgb(270 - t * 90, 0.8, 0.3 + intensity * 0.7);

            default:
                return (255, 255, 255);
        }
    }

    /// <summary>
    ///     Convert HSV to RGB
    /// </summary>
    private static (byte r, byte g, byte b) HsvToRgb(double h, double s, double v)
    {
        h = h % 360;
        if (h < 0) h += 360;

        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;

        double r, g, b;
        if (h < 60)
        {
            r = c;
            g = x;
            b = 0;
        }
        else if (h < 120)
        {
            r = x;
            g = c;
            b = 0;
        }
        else if (h < 180)
        {
            r = 0;
            g = c;
            b = x;
        }
        else if (h < 240)
        {
            r = 0;
            g = x;
            b = c;
        }
        else if (h < 300)
        {
            r = x;
            g = 0;
            b = c;
        }
        else
        {
            r = c;
            g = 0;
            b = x;
        }

        return ((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    /// <summary>
    ///     Get current status
    /// </summary>
    public VisualizerStatus GetStatus()
    {
        return new VisualizerStatus
        {
            IsRunning = IsRunning,
            TargetCanvasName = TargetCanvasName,
            Mode = Mode.ToString(),
            ColorScheme = ColorMode.ToString(),
            Sensitivity = Sensitivity,
            Smoothing = SmoothingFactor
        };
    }

    public class VisualizerStatus
    {
        public bool IsRunning { get; set; }
        public string? TargetCanvasName { get; set; }
        public string Mode { get; set; } = "";
        public string ColorScheme { get; set; } = "";
        public double Sensitivity { get; set; }
        public double Smoothing { get; set; }
    }
}
