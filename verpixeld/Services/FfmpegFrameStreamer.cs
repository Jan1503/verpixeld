using System.Diagnostics;
using CanvasManagement;
using SkiaSharp;

namespace verpixeld.Services;

/// <summary>
///     Reusable FFmpeg-based frame streamer that decodes video into RGB24 frames,
///     double-buffers them, and renders to a <see cref="Canvas"/> at a fixed display rate.
///     
///     Used by <see cref="AlertService"/> and <see cref="LocalCameraService"/> to avoid
///     duplicating ~200 lines of identical FFmpeg-to-canvas plumbing.
/// </summary>
public class FfmpegFrameStreamer : IDisposable
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _frameSize; // width * height * 3 (RGB24)

    // Double-buffering: decode thread writes, display thread reads
    private byte[]? _displayBuffer;
    private readonly object _bufferLock = new();

    // State
    private Process? _ffmpegProcess;
    private CancellationTokenSource? _cts;
    private Task? _streamTask;
    private Task? _displayTask;
    private SKBitmap? _cachedBitmap;
    private volatile bool _streamConnected;
    private volatile bool _disposed;

    /// <summary>True once the first frame has been received from FFmpeg.</summary>
    public bool IsConnected => _streamConnected;

    /// <summary>True while the streamer is actively running.</summary>
    public bool IsRunning => _streamTask != null && !_disposed;

    /// <summary>
    ///     Called for every frame before it is rendered to the canvas.
    ///     Receives the raw RGB24 byte array, width, and height.
    ///     Use this to apply visual effects in-place.
    /// </summary>
    public Action<byte[], int, int>? FrameProcessor { get; set; }

    /// <summary>
    ///     Called once when the first frame arrives (stream connected).
    /// </summary>
    public Action? OnConnected { get; set; }

    public FfmpegFrameStreamer(int width, int height)
    {
        _width = width;
        _height = height;
        _frameSize = width * height * 3;
    }

    /// <summary>
    ///     Start streaming. Launches FFmpeg with the given arguments and begins
    ///     decoding + rendering to the provided canvas.
    /// </summary>
    /// <param name="ffmpegArgs">Full FFmpeg argument string (must output rawvideo RGB24 to pipe:1).</param>
    /// <param name="canvas">The canvas to render frames onto.</param>
    /// <param name="displayFps">Target display refresh rate (default 20).</param>
    /// <param name="logPrefix">Log prefix for console output (e.g. "[ALERT]" or "[LOCALCAM]").</param>
    public void Start(string ffmpegArgs, Canvas canvas, int displayFps = 20, string logPrefix = "[STREAM]")
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FfmpegFrameStreamer));

        _streamConnected = false;
        lock (_bufferLock) { _displayBuffer = null; }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _streamTask = Task.Run(() => DecodeLoopAsync(ffmpegArgs, ct, logPrefix), ct);
        _displayTask = Task.Run(() => DisplayLoopAsync(canvas, ct, displayFps, logPrefix), ct);
    }

    /// <summary>Stop streaming, kill FFmpeg, and wait for background tasks to finish.</summary>
    public void Stop(string logPrefix = "[STREAM]")
    {
        _cts?.Cancel();

        // Wait for tasks
        var tasks = new List<Task>();
        if (_streamTask != null) tasks.Add(_streamTask);
        if (_displayTask != null) tasks.Add(_displayTask);
        try
        {
            if (tasks.Count > 0) Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(3));
        }
        catch (AggregateException) { /* expected: tasks were cancelled */ }

        _streamTask = null;
        _displayTask = null;

        // Kill FFmpeg
        if (_ffmpegProcess != null)
        {
            try
            {
                if (!_ffmpegProcess.HasExited)
                {
                    _ffmpegProcess.Kill(true);
                    _ffmpegProcess.WaitForExit(2000);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{logPrefix} Error stopping FFmpeg: {ex.Message}");
            }

            _ffmpegProcess.Dispose();
            _ffmpegProcess = null;
        }

        _cts?.Dispose();
        _cts = null;

        // Free bitmap memory
        _cachedBitmap?.Dispose();
        _cachedBitmap = null;
    }

    // ── Decode loop: read raw RGB24 frames from FFmpeg stdout ──

    private async Task DecodeLoopAsync(string ffmpegArgs, CancellationToken ct, string logPrefix)
    {
        try
        {
            Console.WriteLine($"{logPrefix} Starting FFmpeg: ffmpeg {ffmpegArgs[..Math.Min(300, ffmpegArgs.Length)]}...");

            var psi = new ProcessStartInfo("ffmpeg", ffmpegArgs)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _ffmpegProcess = Process.Start(psi);
            if (_ffmpegProcess == null)
            {
                Console.WriteLine($"{logPrefix} Failed to start FFmpeg process");
                return;
            }

            // Log stderr asynchronously
            _ = Task.Run(async () =>
            {
                try
                {
                    using var reader = _ffmpegProcess.StandardError;
                    while (!reader.EndOfStream && !ct.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync(ct);
                        if (line != null)
                            Console.WriteLine($"{logPrefix}/FFmpeg] {line}");
                    }
                }
                catch { /* ignore on cancellation */ }
            }, ct);

            var stream = _ffmpegProcess.StandardOutput.BaseStream;
            var readBuffer = new byte[_frameSize];

            while (!ct.IsCancellationRequested)
            {
                var bytesRead = 0;
                while (bytesRead < _frameSize)
                {
                    var read = await stream.ReadAsync(readBuffer.AsMemory(bytesRead, _frameSize - bytesRead), ct);
                    if (read == 0) break; // EOF
                    bytesRead += read;
                }

                if (bytesRead < _frameSize) break; // incomplete frame = stream ended

                if (!_streamConnected)
                {
                    _streamConnected = true;
                    Console.WriteLine($"{logPrefix} Stream connected, first frame received");
                    OnConnected?.Invoke();
                }

                lock (_bufferLock)
                {
                    _displayBuffer ??= new byte[_frameSize];
                    Buffer.BlockCopy(readBuffer, 0, _displayBuffer, 0, _frameSize);
                }
            }
        }
        catch (OperationCanceledException) { /* expected on stop */ }
        catch (Exception ex)
        {
            Console.WriteLine($"{logPrefix} Stream error: {ex.Message}");
        }
        finally
        {
            try
            {
                if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
                {
                    _ffmpegProcess.Kill(true);
                    _ffmpegProcess.WaitForExit(1500);
                }
            }
            catch { }

            try
            {
                if (_ffmpegProcess != null && _ffmpegProcess.HasExited)
                    Console.WriteLine($"{logPrefix} FFmpeg exited with code {_ffmpegProcess.ExitCode}");
            }
            catch { }

            Console.WriteLine($"{logPrefix} Stream ended");
        }
    }

    // ── Display loop: read from double-buffer at fixed rate, render to canvas ──

    private async Task DisplayLoopAsync(Canvas canvas, CancellationToken ct, int fps, string logPrefix)
    {
        try
        {
            var localBuffer = new byte[_frameSize];
            var intervalMs = Math.Max(10, 1000 / fps);

            // Wait for first frame
            while (!ct.IsCancellationRequested && !_streamConnected)
                await Task.Delay(10, ct);

            while (!ct.IsCancellationRequested)
            {
                bool hasFrame;
                lock (_bufferLock)
                {
                    hasFrame = _displayBuffer != null;
                    if (hasFrame)
                        Buffer.BlockCopy(_displayBuffer!, 0, localBuffer, 0, _frameSize);
                }

                if (hasFrame)
                {
                    // Allow caller to apply effects before rendering
                    FrameProcessor?.Invoke(localBuffer, _width, _height);
                    DrawFrame(localBuffer, canvas);
                }

                await Task.Delay(intervalMs, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"{logPrefix} Display loop error: {ex.Message}");
        }
    }

    // ── Render RGB24 frame data to a canvas via SKBitmap ──

    private void DrawFrame(byte[] rgb24Data, Canvas canvas)
    {
        try
        {
            if (_cachedBitmap == null || _cachedBitmap.Width != _width || _cachedBitmap.Height != _height)
            {
                _cachedBitmap?.Dispose();
                _cachedBitmap = new SKBitmap(_width, _height, SKColorType.Rgba8888, SKAlphaType.Opaque);
            }

            unsafe
            {
                var pixels = (uint*)_cachedBitmap.GetPixels().ToPointer();
                var pixelCount = _width * _height;

                for (var i = 0; i < pixelCount; i++)
                {
                    var srcIdx = i * 3;
                    pixels[i] = 0xFF000000 |
                                ((uint)rgb24Data[srcIdx + 2] << 16) |
                                ((uint)rgb24Data[srcIdx + 1] << 8) |
                                rgb24Data[srcIdx];
                }
            }

            canvas.DrawBitmap(_cachedBitmap, 0, 0);
        }
        catch (ObjectDisposedException)
        {
            _cts?.Cancel();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }
}
