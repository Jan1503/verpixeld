using SkiaSharp;

namespace verpixeld.Hardware;

/// <summary>
///     No-op matrix renderer for simulation mode.
///     Allows the full application pipeline to run without LED matrix hardware.
/// </summary>
[LogTag("SIM")]
public class SimulationMatrixRenderer : IMatrixRenderer
{
    private readonly ILogger<SimulationMatrixRenderer> _log;

    public int Width { get; }
    public int Height { get; }

    public SimulationMatrixRenderer(int width, int height, ILogger<SimulationMatrixRenderer> log)
    {
        _log = log;
        Width = width;
        Height = height;
    }

    private bool _initialized;

    public void Initialize()
    {
        if (_initialized)
            return;
        _initialized = true;
        _log.LogInformation("Simulation mode active — no hardware output ({Width}x{Height})", Width, Height);
    }

    public void RenderFrame(SKBitmap bitmap)
    {
        // No-op: frame is composited but not sent to hardware.
        // The FrameRendered event in RenderLoopService still fires,
        // allowing the live preview stream to pick it up.
    }

    public void Shutdown()
    {
        _log.LogInformation("Simulation renderer shut down");
    }
}
