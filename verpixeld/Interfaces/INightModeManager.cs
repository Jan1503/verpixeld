using verpixeld.Layout;

namespace verpixeld.Interfaces;

/// <summary>
///     Manages automatic brightness adjustment based on time schedules
/// </summary>
public interface INightModeManager : IDisposable
{
    /// <summary>
    ///     Gets the current configuration
    /// </summary>
    NightModeConfiguration GetConfiguration();

    /// <summary>
    ///     Updates the night mode configuration
    /// </summary>
    void UpdateConfiguration(NightModeConfiguration config);

    /// <summary>
    ///     Saves the current configuration to disk
    /// </summary>
    void SaveConfiguration();

    /// <summary>
    ///     Gets the current status
    /// </summary>
    (bool isActive, double currentBrightness, double targetBrightness, string mode) GetStatus();

    /// <summary>
    ///     Forces an immediate brightness check and application
    /// </summary>
    void ForceUpdate();

    /// <summary>
    ///     Forces an immediate brightness update without transition
    /// </summary>
    void ForceUpdateImmediate();
}
