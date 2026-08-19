using SkiaSharp;

namespace verpixeld.Services;

/// <summary>
///     Interface for the render loop service that manages frame rendering
/// </summary>
public interface IRenderService
{
    /// <summary>
    ///     Current frames per second
    /// </summary>
    double CurrentFps { get; }

    /// <summary>
    ///     Total frames rendered since startup
    /// </summary>
    long TotalFrames { get; }

    /// <summary>
    ///     Whether the render loop is currently running
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    ///     Event fired when a frame is rendered
    /// </summary>
    event EventHandler<RenderEventArgs>? FrameRendered;

    /// <summary>
    ///     Start the render loop
    /// </summary>
    void Start();

    /// <summary>
    ///     Stop the render loop
    /// </summary>
    void Stop();

    /// <summary>
    ///     Restart the render loop
    /// </summary>
    void Restart();
}

/// <summary>
///     Event args for frame rendered events
/// </summary>
public class RenderEventArgs : EventArgs
{
    /// <summary>
    ///     The rendered frame (DO NOT modify or store - use immediately)
    /// </summary>
    public SKBitmap Frame { get; init; } = null!;

    /// <summary>
    ///     Time taken to render this frame
    /// </summary>
    public TimeSpan RenderTime { get; init; }

    /// <summary>
    ///     Frame number since startup
    /// </summary>
    public long FrameNumber { get; init; }
}
