using CanvasManagement;
using SkiaSharp;
using verpixeld.Hardware;

namespace verpixeld.Services;

/// <summary>
///     Service that manages the render loop between CanvasManager and the matrix hardware
/// </summary>
public class RenderLoopService : IRenderService, IDisposable
{
    private readonly CanvasManager _canvasManager;
    private readonly TimeSpan _forcedGcInterval = TimeSpan.FromMinutes(30);
    private readonly IMatrixRenderer _matrixRenderer;
    private readonly ImageCorrectionService? _imageCorrection;

    private bool _disposed;

    // FPS tracking
    private int _frameCount;

    // Periodic GC scheduling
    private DateTime _lastForcedGc = DateTime.UtcNow;
    private DateTime _lastFpsReport = DateTime.UtcNow;

    /// <summary>
    ///     Creates a new render loop service
    /// </summary>
    public RenderLoopService(CanvasManager canvasManager, IMatrixRenderer matrixRenderer,
        ImageCorrectionService? imageCorrection = null)
    {
        _canvasManager = canvasManager;
        _matrixRenderer = matrixRenderer;
        _imageCorrection = imageCorrection;

        // Subscribe to canvas manager render events
        _canvasManager.RenderCompleted += OnRenderCompleted;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public double CurrentFps { get; private set; }

    public long TotalFrames { get; private set; }

    public bool IsRunning => _canvasManager.GetCanvases.Count > 0;

    public event EventHandler<RenderEventArgs>? FrameRendered;

    /// <summary>
    ///     Start the render loop
    /// </summary>
    public void Start()
    {
        Console.WriteLine("[RENDER] Starting render loop...");
        _matrixRenderer.Initialize();
        _canvasManager.Run();

        // Give the rendering loop time to start
        Thread.Sleep(100);
        Console.WriteLine("[RENDER] Render loop started");
    }

    /// <summary>
    ///     Stop the render loop
    /// </summary>
    public void Stop()
    {
        Console.WriteLine("[RENDER] Stopping render loop...");
        _canvasManager.Stop();
        Console.WriteLine("[RENDER] Render loop stopped");
    }

    /// <summary>
    ///     Restart the render loop
    /// </summary>
    public void Restart()
    {
        Console.WriteLine("[RENDER] Restarting render loop...");
        Stop();
        Thread.Sleep(100);
        Start();
        Console.WriteLine("[RENDER] Render loop restarted");
    }

    /// <summary>
    ///     Handles render completed events from CanvasManager
    /// </summary>
    /// <remarks>
    ///     ⚠️ WARNING: The bitmap is a direct reference to internal state.
    ///     - DO NOT store the bitmap reference
    ///     - DO use it immediately and synchronously
    ///     The one sanctioned in-place edit is the global image correction below: the compositor fully
    ///     rewrites this bitmap every frame, so an in-place LUT never compounds and it lets a SINGLE
    ///     correction govern every output mode AND the web preview (which reads this same reference).
    /// </remarks>
    private void OnRenderCompleted(object? sender, SKBitmap bitmap)
    {
        try
        {
            var startTime = DateTime.UtcNow;

            // Global image correction for ALL output modes + the web preview (no-op when identity).
            // Skipped for renderers that bake the correction into their own higher-depth LUT (network path),
            // so the 8-bit composite is not requantised before its 8->13-bit expansion.
            if (!_matrixRenderer.HandlesColorCorrection)
                _imageCorrection?.Apply(bitmap);

            // Render to matrix hardware
            _matrixRenderer.RenderFrame(bitmap);

            var renderTime = DateTime.UtcNow - startTime;

            // Track FPS
            _frameCount++;
            TotalFrames++;
            var now = DateTime.UtcNow;

            // Report FPS every 30 seconds
            if ((now - _lastFpsReport).TotalSeconds >= 30)
            {
                CurrentFps = _frameCount / 30.0;
                var memory = GC.GetTotalMemory(false) / 1024 / 1024;

                Console.WriteLine($"[RENDER] FPS: {CurrentFps:F1} | Memory: {memory}MB | Total Frames: {TotalFrames}");

                // Periodic forced GC
                if (now - _lastForcedGc >= _forcedGcInterval)
                {
                    GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                    GC.WaitForPendingFinalizers();
                    var memoryAfterGC = GC.GetTotalMemory(true) / 1024 / 1024;
                    Console.WriteLine($"[GC] Forced collection. Memory after: {memoryAfterGC}MB");
                    _lastForcedGc = now;
                }

                _frameCount = 0;
                _lastFpsReport = now;
            }

            // Fire event for any additional processing
            FrameRendered?.Invoke(this, new RenderEventArgs
            {
                Frame = bitmap,
                RenderTime = renderTime,
                FrameNumber = TotalFrames
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RENDER ERROR] {ex.Message}");
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            _canvasManager.RenderCompleted -= OnRenderCompleted;
            _matrixRenderer.Shutdown();
        }

        _disposed = true;
    }
}
