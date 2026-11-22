using CanvasManagement;

namespace verpixeld.MediaPlayer.Audio;

/// <summary>
///     Interface for audio visualization service
/// </summary>
public interface IAudioVisualizerService : IDisposable
{
    /// <summary>
    ///     Visualization mode
    /// </summary>
    AudioVisualizerService.VisualizationMode Mode { get; set; }

    /// <summary>
    ///     Band levels (0–1, low to high) from another device, the audio board. When this is set
    ///     and returns true from <see cref="ExternalAvailable" />, the visualizer needs no local audio.
    /// </summary>
    Func<float[]?>? ExternalBands { get; set; }

    Func<bool>? ExternalAvailable { get; set; }

    /// <summary>True while running on <see cref="ExternalBands" />.</summary>
    bool UsingExternal { get; }

    /// <summary>
    ///     Sensitivity multiplier for visualization
    /// </summary>
    double Sensitivity { get; set; }

    /// <summary>
    ///     Color scheme for visualization
    /// </summary>
    AudioVisualizerService.ColorScheme ColorMode { get; set; }

    /// <summary>
    ///     Whether to mirror bars
    /// </summary>
    bool MirrorBars { get; set; }

    /// <summary>
    ///     Smoothing factor (0 = no smoothing, 1 = max smoothing)
    /// </summary>
    double SmoothingFactor { get; set; }

    /// <summary>
    ///     Whether the visualizer is currently running
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    ///     Name of the canvas being rendered to
    /// </summary>
    string? TargetCanvasName { get; }

    /// <summary>
    ///     Notify that the target canvas has been removed/disposed
    /// </summary>
    void NotifyCanvasRemoved(string canvasName);

    /// <summary>
    ///     Start the visualizer on the specified canvas
    /// </summary>
    Task<bool> StartAsync(Canvas canvas, string canvasName);

    /// <summary>
    ///     Stop the visualizer
    /// </summary>
    Task StopAsync();

    /// <summary>
    ///     Get current visualizer status
    /// </summary>
    AudioVisualizerService.VisualizerStatus GetStatus();
}
