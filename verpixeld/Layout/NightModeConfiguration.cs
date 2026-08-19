using System.Text.Json.Serialization;

namespace verpixeld.Layout;

/// <summary>
///     Configuration for automatic night mode brightness scheduling
/// </summary>
public class NightModeConfiguration
{
    /// <summary>
    ///     Whether night mode is enabled
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    /// <summary>
    ///     Start time for night mode (24-hour format: "HH:mm")
    /// </summary>
    [JsonPropertyName("startTime")]
    public string StartTime { get; set; } = "22:00";

    /// <summary>
    ///     End time for night mode (24-hour format: "HH:mm")
    /// </summary>
    [JsonPropertyName("endTime")]
    public string EndTime { get; set; } = "07:00";

    /// <summary>
    ///     Brightness level during night mode (0.0 - 1.0)
    /// </summary>
    [JsonPropertyName("nightBrightness")]
    public double NightBrightness { get; set; } = 0.2;

    /// <summary>
    ///     Brightness level during day mode (0.0 - 1.0)
    /// </summary>
    [JsonPropertyName("dayBrightness")]
    public double DayBrightness { get; set; } = 1.0;

    /// <summary>
    ///     Transition duration in minutes (gradual fade)
    /// </summary>
    [JsonPropertyName("transitionMinutes")]
    public int TransitionMinutes { get; set; } = 5;

    /// <summary>
    ///     Days of the week when night mode is active (0=Sunday, 6=Saturday)
    ///     Empty array means all days
    /// </summary>
    [JsonPropertyName("activeDays")]
    public List<int> ActiveDays { get; set; } = new();

    /// <summary>
    ///     Last time the configuration was modified
    /// </summary>
    [JsonPropertyName("lastModified")]
    public DateTime LastModified { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     Check if night mode should be active at a specific time
    /// </summary>
    public bool IsNightModeActive(DateTime time)
    {
        if (!Enabled) return false;

        // Check if day is active (empty list means all days)
        if (ActiveDays.Count > 0 && !ActiveDays.Contains((int)time.DayOfWeek)) return false;

        var currentTime = time.TimeOfDay;
        var start = TimeSpan.Parse(StartTime);
        var end = TimeSpan.Parse(EndTime);

        // Handle overnight schedules (e.g., 22:00 to 07:00)
        if (start > end) return currentTime >= start || currentTime < end;

        return currentTime >= start && currentTime < end;
    }

    /// <summary>
    ///     Get the target brightness for a specific time
    /// </summary>
    public double GetTargetBrightness(DateTime time)
    {
        return IsNightModeActive(time) ? NightBrightness : DayBrightness;
    }
}
