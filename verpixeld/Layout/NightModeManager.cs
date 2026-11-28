using System.Text.Json;
using CanvasManagement;
using verpixeld.Configuration;
using verpixeld.Interfaces;
using verpixeld.Services;

namespace verpixeld.Layout;

/// <summary>
///     Manages automatic brightness adjustment based on time schedules
/// </summary>
[LogTag("NIGHT MODE")]
public class NightModeManager : INightModeManager
{
    private readonly ILogger<NightModeManager> _log;

    private readonly CanvasManager _canvasManager;
    private readonly string _configPath;
    private readonly object _lock = new();
    private Timer? _checkTimer;
    private NightModeConfiguration _config;
    private bool _isTransitioning;
    private volatile bool _wallOff;

    public bool WallOff
    {
        get => _wallOff;
        set => _wallOff = value;
    }

    /// <summary>
    ///     Switched off by hand and still dark: the schedule keeps out. A brightness set elsewhere since
    ///     (web UI, Home Assistant) counts as switched on again.
    /// </summary>
    private bool HeldOff()
    {
        if (!_wallOff) return false;
        if (_canvasManager.Brightness <= 0.005f) return true;
        _wallOff = false;
        return false;
    }

    public NightModeManager(CanvasManager canvasManager, ILogger<NightModeManager> log, string? configDirectory = null)
    {
        _log = log;
        _canvasManager = canvasManager ?? throw new ArgumentNullException(nameof(canvasManager));

        _configPath = configDirectory != null ? Path.Combine(configDirectory, "nightmode.json") : AppPaths.NightModeConfig;
        _config = LoadConfiguration();

        _log.LogInformation("Initialized. Enabled: {Enabled}", _config.Enabled);
        if (_config.Enabled)
        {
            _log.LogInformation("Schedule: {StartTime} - {EndTime}", _config.StartTime, _config.EndTime);
            _log.LogInformation("Day brightness: {DayBrightness:F2}, Night brightness: {NightBrightness:F2}", _config.DayBrightness, _config.NightBrightness);
            _log.LogInformation("Transition duration: {TransitionMinutes} minutes", _config.TransitionMinutes);
            _log.LogInformation("Active days: {Value}", (_config.ActiveDays.Count == 0 ? "All days" : string.Join(", ", _config.ActiveDays)));
        }

        // Start the monitoring timer with a 5-second delay to ensure CanvasManager is ready
        // Then check every minute
        _checkTimer = new Timer(CheckAndApplyBrightness, null, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1));

        _log.LogInformation("Timer started - first check in 5 seconds, then every minute");
    }

    public void Dispose()
    {
        _checkTimer?.Dispose();
        _checkTimer = null;
    }

    /// <summary>
    ///     Save night mode configuration to disk
    /// </summary>
    public void SaveConfiguration()
    {
        try
        {
            _config.LastModified = DateTime.UtcNow;

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            var json = JsonSerializer.Serialize(_config, options);
            FileHelper.AtomicWriteAllText(_configPath, json);

            _log.LogInformation("Configuration saved to {ConfigPath}", _configPath);
        }
        catch (Exception ex)
        {
            _log.LogError("Error saving configuration: {Message}", ex.Message);
            throw;
        }
    }

    /// <summary>
    ///     Get current configuration
    /// </summary>
    public NightModeConfiguration GetConfiguration()
    {
        lock (_lock)
        {
            return _config;
        }
    }

    /// <summary>
    ///     Update night mode configuration
    /// </summary>
    public void UpdateConfiguration(NightModeConfiguration newConfig)
    {
        lock (_lock)
        {
            _config = newConfig ?? throw new ArgumentNullException(nameof(newConfig));
            SaveConfiguration();

            // Immediately check and apply new settings
            CheckAndApplyBrightness(null);

            _log.LogInformation("Configuration updated. Enabled: {Enabled}", _config.Enabled);
        }
    }

    /// <summary>
    ///     Force an immediate brightness check and update
    /// </summary>
    public void ForceUpdate()
    {
        _log.LogInformation("Force update requested");
        CheckAndApplyBrightness(null);
    }

    /// <summary>
    ///     Force an immediate brightness update without transition (for testing)
    /// </summary>
    public void ForceUpdateImmediate()
    {
        lock (_lock)
        {
            _log.LogInformation("Force immediate update requested (bypassing transition)");

            if (!_config.Enabled)
            {
                _log.LogWarning("Night mode is disabled - cannot apply");
                return;
            }
            if (HeldOff())
            {
                _log.LogInformation("Wall is switched off - not applied");
                return;
            }

            var now = DateTime.Now;
            var targetBrightness = _config.GetTargetBrightness(now);
            var isNightMode = _config.IsNightModeActive(now);
            var mode = isNightMode ? "NIGHT" : "DAY";

            _log.LogInformation("Applying {Mode} mode brightness: {TargetBrightness:F2}", mode, targetBrightness);
            _canvasManager.Brightness = (float)targetBrightness;
            _log.LogInformation("Brightness immediately set to {TargetBrightness:F2}", targetBrightness);
        }
    }

    /// <summary>
    ///     Get current night mode status
    /// </summary>
    public (bool isActive, double currentBrightness, double targetBrightness, string mode) GetStatus()
    {
        lock (_lock)
        {
            var now = DateTime.Now;
            var isActive = _config.IsNightModeActive(now);
            var currentBrightness = _canvasManager.Brightness;
            var targetBrightness = _config.GetTargetBrightness(now);
            var mode = isActive ? "night" : "day";

            return (isActive, currentBrightness, targetBrightness, mode);
        }
    }

    /// <summary>
    ///     Load night mode configuration from disk
    /// </summary>
    private NightModeConfiguration LoadConfiguration()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                var config = JsonSerializer.Deserialize<NightModeConfiguration>(json);
                if (config != null)
                {
                    _log.LogInformation("Configuration loaded from {ConfigPath}", _configPath);
                    return config;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError("Error loading configuration: {Message}", ex.Message);
        }

        _log.LogInformation("Using default configuration");
        return new NightModeConfiguration();
    }

    /// <summary>
    ///     Check current time and apply appropriate brightness
    /// </summary>
    private void CheckAndApplyBrightness(object? state)
    {
        if (_isTransitioning)
        {
            _log.LogDebug("Skipping check - transition in progress");
            return;
        }

        lock (_lock)
        {
            _log.LogDebug("Checking brightness at {Now:HH:mm:ss}, enabled: {Enabled}", DateTime.Now, _config.Enabled);

            if (!_config.Enabled)
            {
                _log.LogDebug("Skipping - night mode disabled");
                return;
            }
            if (HeldOff())
            {
                _log.LogDebug("Wall is switched off on the panel - schedule waits");
                return;
            }

            var now = DateTime.Now;
            var targetBrightness = _config.GetTargetBrightness(now);
            var currentBrightness = _canvasManager.Brightness;
            var isNightMode = _config.IsNightModeActive(now);
            var mode = isNightMode ? "NIGHT" : "DAY";

            _log.LogDebug("{Mode} ({StartTime} - {EndTime}): brightness {CurrentBrightness:F2}, target {TargetBrightness:F2}",
                mode, _config.StartTime, _config.EndTime, currentBrightness, targetBrightness);

            // Check if we need to adjust brightness
            if (Math.Abs(currentBrightness - targetBrightness) > 0.01)
            {
                _log.LogInformation("{Mode} mode active. Adjusting brightness: {CurrentBrightness:F2} ? {TargetBrightness:F2}", mode, currentBrightness, targetBrightness);

                if (_config.TransitionMinutes > 0)
                {
                    _log.LogInformation("Starting {TransitionMinutes}-minute transition", _config.TransitionMinutes);
                    // Start gradual transition
                    _ = TransitionBrightnessAsync(currentBrightness, targetBrightness, _config.TransitionMinutes);
                }
                else
                {
                    // Immediate change
                    _log.LogInformation("Applying immediate brightness change");
                    _canvasManager.Brightness = (float)targetBrightness;
                    _log.LogInformation("Brightness set to {TargetBrightness:F2}", targetBrightness);
                }
            }
            else
            {
                _log.LogDebug("Brightness already at target - no adjustment needed");
            }
        }
    }

    /// <summary>
    ///     Gradually transition brightness over time
    /// </summary>
    private async Task TransitionBrightnessAsync(double from, double to, int durationMinutes)
    {
        _isTransitioning = true;

        try
        {
            var steps = durationMinutes * 6; // 6 steps per minute (every 10 seconds)
            var stepDelay = TimeSpan.FromSeconds(10);
            var stepSize = (to - from) / steps;

            _log.LogInformation("Starting transition: {From:F2} ? {To:F2} over {DurationMinutes} minutes ({Steps} steps)", from, to, durationMinutes, steps);

            for (var i = 1; i <= steps; i++)
            {
                if (_wallOff)
                {
                    _log.LogInformation("Transition stopped: the wall was switched off");
                    break;
                }
                var newBrightness = from + stepSize * i;
                _canvasManager.Brightness = (float)newBrightness;

                if (i % 6 == 0 || i == steps) // Log every minute and at the end
                    _log.LogInformation("Transition progress: {NewBrightness:F2} ({I}/{Steps})", newBrightness, i, steps);

                await Task.Delay(stepDelay);
            }

            _log.LogInformation("Transition complete: {To:F2}", to);
        }
        catch (Exception ex)
        {
            _log.LogError("Error during transition: {Message}", ex.Message);
        }
        finally
        {
            _isTransitioning = false;
        }
    }
}
