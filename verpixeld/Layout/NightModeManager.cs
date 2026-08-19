using System.Text.Json;
using CanvasManagement;
using verpixeld.Configuration;
using verpixeld.Interfaces;
using verpixeld.Services;

namespace verpixeld.Layout;

/// <summary>
///     Manages automatic brightness adjustment based on time schedules
/// </summary>
public class NightModeManager : INightModeManager
{
    private readonly CanvasManager _canvasManager;
    private readonly string _configPath;
    private readonly object _lock = new();
    private Timer? _checkTimer;
    private NightModeConfiguration _config;
    private bool _isTransitioning;

    public NightModeManager(CanvasManager canvasManager, string? configDirectory = null)
    {
        _canvasManager = canvasManager ?? throw new ArgumentNullException(nameof(canvasManager));

        _configPath = configDirectory != null ? Path.Combine(configDirectory, "nightmode.json") : AppPaths.NightModeConfig;
        _config = LoadConfiguration();

        Console.WriteLine($"[NIGHT MODE] Initialized. Enabled: {_config.Enabled}");
        if (_config.Enabled)
        {
            Console.WriteLine($"[NIGHT MODE] Schedule: {_config.StartTime} - {_config.EndTime}");
            Console.WriteLine(
                $"[NIGHT MODE] Day brightness: {_config.DayBrightness:F2}, Night brightness: {_config.NightBrightness:F2}");
            Console.WriteLine($"[NIGHT MODE] Transition duration: {_config.TransitionMinutes} minutes");
            Console.WriteLine(
                $"[NIGHT MODE] Active days: {(_config.ActiveDays.Count == 0 ? "All days" : string.Join(", ", _config.ActiveDays))}");
        }

        // Start the monitoring timer with a 5-second delay to ensure CanvasManager is ready
        // Then check every minute
        _checkTimer = new Timer(CheckAndApplyBrightness, null, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1));

        Console.WriteLine("[NIGHT MODE] Timer started - first check in 5 seconds, then every minute");
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

            Console.WriteLine($"[NIGHT MODE] Configuration saved to {_configPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NIGHT MODE] Error saving configuration: {ex.Message}");
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

            Console.WriteLine($"[NIGHT MODE] Configuration updated. Enabled: {_config.Enabled}");
        }
    }

    /// <summary>
    ///     Force an immediate brightness check and update
    /// </summary>
    public void ForceUpdate()
    {
        Console.WriteLine("[NIGHT MODE] Force update requested");
        CheckAndApplyBrightness(null);
    }

    /// <summary>
    ///     Force an immediate brightness update without transition (for testing)
    /// </summary>
    public void ForceUpdateImmediate()
    {
        lock (_lock)
        {
            Console.WriteLine("[NIGHT MODE] Force immediate update requested (bypassing transition)");

            if (!_config.Enabled)
            {
                Console.WriteLine("[NIGHT MODE] Night mode is disabled - cannot apply");
                return;
            }

            var now = DateTime.Now;
            var targetBrightness = _config.GetTargetBrightness(now);
            var isNightMode = _config.IsNightModeActive(now);
            var mode = isNightMode ? "NIGHT" : "DAY";

            Console.WriteLine($"[NIGHT MODE] Applying {mode} mode brightness: {targetBrightness:F2}");
            _canvasManager.Brightness = (float)targetBrightness;
            Console.WriteLine($"[NIGHT MODE] Brightness immediately set to {targetBrightness:F2}");
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
                    Console.WriteLine($"[NIGHT MODE] Configuration loaded from {_configPath}");
                    return config;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NIGHT MODE] Error loading configuration: {ex.Message}");
        }

        Console.WriteLine("[NIGHT MODE] Using default configuration");
        return new NightModeConfiguration();
    }

    /// <summary>
    ///     Check current time and apply appropriate brightness
    /// </summary>
    private void CheckAndApplyBrightness(object? state)
    {
        if (_isTransitioning)
        {
            Console.WriteLine("[NIGHT MODE] Skipping check - transition in progress");
            return;
        }

        lock (_lock)
        {
            Console.WriteLine($"[NIGHT MODE] Checking brightness at {DateTime.Now:HH:mm:ss}");
            Console.WriteLine($"[NIGHT MODE]   Enabled: {_config.Enabled}");

            if (!_config.Enabled)
            {
                Console.WriteLine("[NIGHT MODE]   Skipping - night mode disabled");
                return;
            }

            var now = DateTime.Now;
            var targetBrightness = _config.GetTargetBrightness(now);
            var currentBrightness = _canvasManager.Brightness;
            var isNightMode = _config.IsNightModeActive(now);
            var mode = isNightMode ? "NIGHT" : "DAY";

            Console.WriteLine($"[NIGHT MODE]   Current time: {now:HH:mm:ss}");
            Console.WriteLine($"[NIGHT MODE]   Schedule: {_config.StartTime} - {_config.EndTime}");
            Console.WriteLine($"[NIGHT MODE]   Mode: {mode}");
            Console.WriteLine($"[NIGHT MODE]   Current brightness: {currentBrightness:F2}");
            Console.WriteLine($"[NIGHT MODE]   Target brightness: {targetBrightness:F2}");
            Console.WriteLine($"[NIGHT MODE]   Difference: {Math.Abs(currentBrightness - targetBrightness):F2}");

            // Check if we need to adjust brightness
            if (Math.Abs(currentBrightness - targetBrightness) > 0.01)
            {
                Console.WriteLine(
                    $"[NIGHT MODE] {mode} mode active. Adjusting brightness: {currentBrightness:F2} ? {targetBrightness:F2}");

                if (_config.TransitionMinutes > 0)
                {
                    Console.WriteLine($"[NIGHT MODE] Starting {_config.TransitionMinutes}-minute transition");
                    // Start gradual transition
                    _ = TransitionBrightnessAsync(currentBrightness, targetBrightness, _config.TransitionMinutes);
                }
                else
                {
                    // Immediate change
                    Console.WriteLine("[NIGHT MODE] Applying immediate brightness change");
                    _canvasManager.Brightness = (float)targetBrightness;
                    Console.WriteLine($"[NIGHT MODE] Brightness set to {targetBrightness:F2}");
                }
            }
            else
            {
                Console.WriteLine("[NIGHT MODE] Brightness already at target - no adjustment needed");
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

            Console.WriteLine(
                $"[NIGHT MODE] Starting transition: {from:F2} ? {to:F2} over {durationMinutes} minutes ({steps} steps)");

            for (var i = 1; i <= steps; i++)
            {
                var newBrightness = from + stepSize * i;
                _canvasManager.Brightness = (float)newBrightness;

                if (i % 6 == 0 || i == steps) // Log every minute and at the end
                    Console.WriteLine($"[NIGHT MODE] Transition progress: {newBrightness:F2} ({i}/{steps})");

                await Task.Delay(stepDelay);
            }

            Console.WriteLine($"[NIGHT MODE] Transition complete: {to:F2}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NIGHT MODE] Error during transition: {ex.Message}");
        }
        finally
        {
            _isTransitioning = false;
        }
    }
}
