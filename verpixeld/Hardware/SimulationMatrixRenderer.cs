using SkiaSharp;

namespace verpixeld.Hardware;

/// <summary>
///     No-op matrix renderer for simulation mode.
///     Allows the full application pipeline to run without LED matrix hardware.
/// </summary>
public class SimulationMatrixRenderer : IMatrixRenderer
{
    public int Width { get; }
    public int Height { get; }

    public SimulationMatrixRenderer(int width, int height)
    {
        Width = width;
        Height = height;
    }

    private bool _initialized;

    public void Initialize()
    {
        if (_initialized)
            return;
        _initialized = true;
        Console.WriteLine($"[SIM] Simulation mode active — no hardware output ({Width}x{Height})");
    }

    public void RenderFrame(SKBitmap bitmap)
    {
        // No-op: frame is composited but not sent to hardware.
        // The FrameRendered event in RenderLoopService still fires,
        // allowing the live preview stream to pick it up.
    }

    public void Shutdown()
    {
        Console.WriteLine("[SIM] Simulation renderer shut down");
    }
}
