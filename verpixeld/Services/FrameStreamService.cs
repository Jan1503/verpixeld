using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;

namespace verpixeld.Services;

/// <summary>
///     Captures rendered frames and serves them as an MJPEG stream or single snapshots.
///     Subscribes to <see cref="IRenderService.FrameRendered" /> and encodes frames to JPEG
///     only when at least one client is connected (zero overhead otherwise).
///
///     IMPORTANT: JPEG encoding does NOT run on the render thread. The frame handler only does a fast
///     raw-pixel copy (tens of microseconds) and signals a dedicated background encoder thread. Previously
///     the encode ran synchronously inside the render loop's swap lock, so every ~66 ms the whole loop
///     stalled for the duration of the encode — which showed up as micro-stutter on BOTH the HDMI output
///     and the web preview whenever the GUI was open.
/// </summary>
public class FrameStreamService : IDisposable
{
    private readonly IRenderService _renderService;
    private readonly object _frameLock = new();

    // Latest encoded JPEG frame (shared across all clients)
    private byte[]? _latestJpeg;
    private long _latestFrameNumber;

    // Throttle: minimum interval between encodes (~15fps)
    private DateTime _lastEncodeTime = DateTime.MinValue;
    private static readonly TimeSpan MinEncodeInterval = TimeSpan.FromMilliseconds(66); // ~15fps

    // JPEG quality (1-100)
    private const int JpegQuality = 75;

    // Active client tracking
    private volatile int _clientCount;

    // Signal for new (encoded) frame availability, consumed by connected stream clients.
    private readonly ManualResetEventSlim _newFrameSignal = new(false);

    // --- Off-thread encode handoff ---------------------------------------------------------------
    // The render thread copies raw pixels into _rawFrame under _rawLock and signals _encodeSignal.
    // The background encoder thread picks them up, encodes, and publishes _latestJpeg.
    private readonly object _rawLock = new();
    private readonly ManualResetEventSlim _encodeSignal = new(false);
    private byte[] _rawFrame = [];
    private int _rawWidth;
    private int _rawHeight;
    private long _rawFrameNumber;
    private bool _rawPending;
    private readonly Thread _encoderThread;
    private volatile bool _running = true;

    private bool _disposed;

    public FrameStreamService(IRenderService renderService)
    {
        _renderService = renderService;
        _renderService.FrameRendered += OnFrameRendered;

        _encoderThread = new Thread(EncodeLoop)
        {
            IsBackground = true,
            Name = "MjpegEncoder",
            // Below normal so a burst of encoding never competes with the render thread for a core.
            Priority = ThreadPriority.BelowNormal
        };
        _encoderThread.Start();
    }

    /// <summary>Number of currently connected MJPEG stream clients.</summary>
    public int ClientCount => _clientCount;

    /// <summary>Whether any clients are connected.</summary>
    public bool HasClients => _clientCount > 0;

    /// <summary>
    ///     Returns the latest JPEG-encoded frame, or null if no frame has been captured yet.
    /// </summary>
    public byte[]? GetLatestFrame()
    {
        lock (_frameLock)
        {
            return _latestJpeg;
        }
    }

    /// <summary>
    ///     Stream frames as MJPEG to the given HTTP response.
    ///     This method blocks until the cancellation token is triggered (client disconnects).
    /// </summary>
    public async Task StreamFramesAsync(HttpResponse response, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _clientCount);
        Console.WriteLine($"[PREVIEW] Client connected (total: {_clientCount})");

        try
        {
            response.ContentType = "multipart/x-mixed-replace; boundary=frame";
            response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            response.Headers.Pragma = "no-cache";

            var boundary = Encoding.UTF8.GetBytes("\r\n--frame\r\nContent-Type: image/jpeg\r\nContent-Length: ");
            var newline = Encoding.UTF8.GetBytes("\r\n\r\n");
            long lastSentFrame = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                // Wait for a new frame (with timeout to check cancellation)
                _newFrameSignal.Wait(200, cancellationToken);
                _newFrameSignal.Reset();

                byte[]? jpeg;
                long frameNumber;

                lock (_frameLock)
                {
                    jpeg = _latestJpeg;
                    frameNumber = _latestFrameNumber;
                }

                // Skip if no frame yet or same frame as last sent
                if (jpeg == null || frameNumber == lastSentFrame)
                    continue;

                lastSentFrame = frameNumber;

                try
                {
                    // Write MJPEG boundary + JPEG data
                    await response.Body.WriteAsync(boundary, cancellationToken);
                    await response.Body.WriteAsync(
                        Encoding.UTF8.GetBytes(jpeg.Length.ToString()), cancellationToken);
                    await response.Body.WriteAsync(newline, cancellationToken);
                    await response.Body.WriteAsync(jpeg, cancellationToken);
                    await response.Body.FlushAsync(cancellationToken);
                }
                catch (IOException)
                {
                    // Client disconnected
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal disconnection
        }
        finally
        {
            var remaining = Interlocked.Decrement(ref _clientCount);
            Console.WriteLine($"[PREVIEW] Client disconnected (remaining: {remaining})");
        }
    }

    /// <summary>
    ///     Handler for the FrameRendered event. Runs on the render thread, so it must be cheap: it only
    ///     copies the raw pixels into a handoff buffer and wakes the encoder thread. No JPEG work here.
    ///     Only active when clients are connected, and throttled to ~15 fps.
    /// </summary>
    private void OnFrameRendered(object? sender, RenderEventArgs e)
    {
        // Zero-overhead guard: skip entirely if nobody is watching
        if (_clientCount == 0)
            return;

        // Throttle encoding rate
        var now = DateTime.UtcNow;
        if (now - _lastEncodeTime < MinEncodeInterval)
            return;
        _lastEncodeTime = now;

        var bitmap = e.Frame;
        var src = bitmap.GetPixels();
        if (src == IntPtr.Zero)
            return;

        var len = bitmap.RowBytes * bitmap.Height;

        lock (_rawLock)
        {
            if (_rawFrame.Length != len)
                _rawFrame = new byte[len];

            Marshal.Copy(src, _rawFrame, 0, len);
            _rawWidth = bitmap.Width;
            _rawHeight = bitmap.Height;
            _rawFrameNumber = e.FrameNumber;
            _rawPending = true;
        }

        _encodeSignal.Set();
    }

    /// <summary>
    ///     Background loop: waits for a raw frame handed off by the render thread, encodes it to JPEG,
    ///     and publishes it. All the CPU-heavy work happens here, off the render thread.
    /// </summary>
    private void EncodeLoop()
    {
        SKBitmap? encBitmap = null;
        var localRaw = Array.Empty<byte>();

        while (_running)
        {
            try
            {
                _encodeSignal.Wait();
                _encodeSignal.Reset();
                if (!_running)
                    break;

                int w, h;
                long frameNumber;

                lock (_rawLock)
                {
                    if (!_rawPending)
                        continue;
                    _rawPending = false;

                    w = _rawWidth;
                    h = _rawHeight;
                    frameNumber = _rawFrameNumber;

                    if (localRaw.Length != _rawFrame.Length)
                        localRaw = new byte[_rawFrame.Length];
                    Buffer.BlockCopy(_rawFrame, 0, localRaw, 0, _rawFrame.Length);
                }

                if (w <= 0 || h <= 0 || localRaw.Length == 0)
                    continue;

                if (encBitmap == null || encBitmap.Width != w || encBitmap.Height != h)
                {
                    encBitmap?.Dispose();
                    encBitmap = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
                }

                Marshal.Copy(localRaw, 0, encBitmap.GetPixels(), localRaw.Length);

                using var image = SKImage.FromBitmap(encBitmap);
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
                var jpeg = data.ToArray();

                lock (_frameLock)
                {
                    _latestJpeg = jpeg;
                    _latestFrameNumber = frameNumber;
                }

                _newFrameSignal.Set();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PREVIEW] Frame encode error: {ex.Message}");
            }
        }

        encBitmap?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _renderService.FrameRendered -= OnFrameRendered;

        _running = false;
        _encodeSignal.Set();
        try
        {
            _encoderThread.Join(1000);
        }
        catch
        {
            // ignore join failures on shutdown
        }

        _encodeSignal.Dispose();
        _newFrameSignal.Dispose();
        GC.SuppressFinalize(this);
    }
}
