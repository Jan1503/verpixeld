using System.Text.Json.Serialization;

namespace verpixeld.Layout;

/// <summary>
///     Represents a scheduled layout change
/// </summary>
public class LayoutScheduleEntry
{
    /// <summary>
    ///     Unique identifier for this schedule entry
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    ///     Name of the saved layout to load
    /// </summary>
    [JsonPropertyName("layoutName")]
    public string LayoutName { get; set; } = string.Empty;

    /// <summary>
    ///     Time to trigger the layout change (24-hour format HH:mm)
    /// </summary>
    [JsonPropertyName("time")]
    public string Time { get; set; } = "00:00";

    /// <summary>
    ///     Days of the week when this schedule is active (0 = Sunday, 6 = Saturday)
    ///     Empty list means all days
    /// </summary>
    [JsonPropertyName("activeDays")]
    public List<int> ActiveDays { get; set; } = new();

    /// <summary>
    ///     Whether this schedule entry is enabled
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Optional description of what this schedule does
    /// </summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    ///     Last time this schedule was triggered
    /// </summary>
    [JsonPropertyName("lastTriggered")]
    public DateTime? LastTriggered { get; set; }

    /// <summary>
    ///     Creation timestamp
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
///     Collection of scheduled layout changes
/// </summary>
public class LayoutSchedule
{
    /// <summary>
    ///     Name of this schedule (e.g., "Weekday Schedule", "Weekend Schedule")
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "Default Schedule";

    /// <summary>
    ///     Whether this schedule is enabled globally
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Whether this is the default schedule to load on startup
    /// </summary>
    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; set; } = false;

    /// <summary>
    ///     List of scheduled entries
    /// </summary>
    [JsonPropertyName("entries")]
    public List<LayoutScheduleEntry> Entries { get; set; } = new();

    /// <summary>
    ///     When this schedule was created
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     When this schedule was last modified
    /// </summary>
    [JsonPropertyName("lastModified")]
    public DateTime LastModified { get; set; } = DateTime.UtcNow;
}
