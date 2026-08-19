using SkiaSharp;

namespace verpixeld.Hardware;

/// <summary>
///     Interface for rendering frames to the LED matrix hardware
/// </summary>
public interface IMatrixRenderer
{
    /// <summary>
    ///     Width of the display in pixels
    /// </summary>
    int Width { get; }

    /// <summary>
    ///     Height of the display in pixels
    /// </summary>
    int Height { get; }

    /// <summary>
    ///     Initialize the matrix hardware
    /// </summary>
    void Initialize();

    /// <summary>
    ///     True if this renderer applies the global image correction itself (at its own, higher output bit
    ///     depth) and therefore the render loop must NOT pre-correct the shared 8-bit bitmap. The network
    ///     path sets this so gamma/contrast/etc. are baked into the 8-bit -> 13-bit LUT (no 8-bit requantise).
    /// </summary>
    bool HandlesColorCorrection => false;

    /// <summary>
    ///     Render a frame to the matrix
    /// </summary>
    /// <param name="bitmap">The bitmap to render</param>
    void RenderFrame(SKBitmap bitmap);

    /// <summary>
    ///     Dispose of hardware resources
    /// </summary>
    void Shutdown();
}
