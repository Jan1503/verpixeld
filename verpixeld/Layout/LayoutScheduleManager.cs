using System.Text.Json;
using verpixeld.Configuration;
using verpixeld.Interfaces;
using verpixeld.Services;

namespace verpixeld.Layout;

/// <summary>
///     Manages layout scheduling - automatic layout switching based on time
/// </summary>
[LogTag("SCHEDULER")]
public class LayoutScheduleManager : ILayoutScheduleManager
{
    private readonly ILogger<LayoutScheduleManager> _log;

    private readonly LayoutStorageManager _layoutStorage;
    private readonly string _schedulesDirectory;
    private LayoutSchedule? _activeSchedule;
    private DateTime _lastCheckTime;
    private Timer? _schedulerTimer;

    public LayoutScheduleManager(LayoutStorageManager layoutStorage, ILogger<LayoutScheduleManager> log)
    {
        _log = log;
        _layoutStorage = layoutStorage;

        _schedulesDirectory = AppPaths.SchedulesDir;

        _lastCheckTime = DateTime.Now;
    }

    // Event fired when a scheduled layout change occurs
    public event EventHandler<LayoutScheduleTriggeredEventArgs>? ScheduleTriggered;

    /// <summary>
    ///     Start the scheduler service
    /// </summary>
    public void Start()
    {
        Stop(); // Ensure any existing timer is stopped

        // Load default schedule if exists
        var defaultSchedule = GetDefaultSchedule();
        if (defaultSchedule != null && defaultSchedule.Enabled)
        {
            _activeSchedule = defaultSchedule;
            _log.LogInformation("Loaded default schedule: '{Name}' with {Count} entries", _activeSchedule.Name, _activeSchedule.Entries.Count);
        }
        else
        {
            _log.LogInformation("No default schedule found");
        }

        // ✅ Calculate delay to next minute boundary for precise timing
        var now = DateTime.Now;
        var nextMinute = now.Date.AddHours(now.Hour).AddMinutes(now.Minute + 1);
        var initialDelay = nextMinute - now;

        _log.LogInformation("Current time: {Now:HH:mm:ss}", now);
        _log.LogInformation("Next check at: {NextMinute:HH:mm:ss} (in {TotalSeconds:F1}s)", nextMinute, initialDelay.TotalSeconds);

        // Start timer: fire at next minute boundary, then every minute
        _schedulerTimer = new Timer(
            CheckSchedules,
            null,
            initialDelay, // ✅ Delay until next minute
            TimeSpan.FromMinutes(1) // Then every minute
        );

        _log.LogInformation("Scheduler started (synchronized to minute boundaries)");
    }

    /// <summary>
    ///     Stop the scheduler service
    /// </summary>
    public void Stop()
    {
        _schedulerTimer?.Dispose();
        _schedulerTimer = null;
        _log.LogInformation("Scheduler stopped");
    }

    /// <summary>
    ///     Save a schedule to disk
    /// </summary>
    public bool SaveSchedule(LayoutSchedule schedule)
    {
        try
        {
            schedule.LastModified = DateTime.UtcNow;

            var filename = SanitizeFilename(schedule.Name) + ".json";
            var filepath = Path.Combine(_schedulesDirectory, filename);

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            var json = JsonSerializer.Serialize(schedule, options);
            FileHelper.AtomicWriteAllText(filepath, json);

            _log.LogInformation("Saved schedule '{Name}' to {Filepath}", schedule.Name, filepath);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError("Error saving schedule '{Name}': {Message}", schedule.Name, ex.Message);
            return false;
        }
    }

    /// <summary>
    ///     Load a schedule from disk
    /// </summary>
    public LayoutSchedule? LoadSchedule(string scheduleName)
    {
        try
        {
            var filename = SanitizeFilename(scheduleName) + ".json";
            var filepath = Path.Combine(_schedulesDirectory, filename);

            if (!File.Exists(filepath))
            {
                _log.LogWarning("Schedule file not found: {Filepath}", filepath);
                return null;
            }

            var json = File.ReadAllText(filepath);

            // Use same options as SaveSchedule for consistency
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            var schedule = JsonSerializer.Deserialize<LayoutSchedule>(json, options);

            _log.LogInformation("Loaded schedule '{Name}' from {Filepath}", schedule?.Name, filepath);
            return schedule;
        }
        catch (Exception ex)
        {
            _log.LogError("Error loading schedule '{ScheduleName}': {Message}", scheduleName, ex.Message);
            return null;
        }
    }

    /// <summary>
    ///     Get a schedule by name (alias for LoadSchedule)
    /// </summary>
    public LayoutSchedule? GetSchedule(string scheduleName)
    {
        return LoadSchedule(scheduleName);
    }

    /// <summary>
    ///     Get all saved schedules
    /// </summary>
    public List<LayoutSchedule> GetAllSchedules()
    {
        var schedules = new List<LayoutSchedule>();

        try
        {
            if (!Directory.Exists(_schedulesDirectory))
                return schedules;

            var files = Directory.GetFiles(_schedulesDirectory, "*.json");

            // Use consistent options for deserialization
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            foreach (var file in files)
                try
                {
                    var json = File.ReadAllText(file);
                    var schedule = JsonSerializer.Deserialize<LayoutSchedule>(json, options);

                    if (schedule != null)
                        schedules.Add(schedule);
                }
                catch (Exception ex)
                {
                    _log.LogError("Error loading schedule from {File}: {Message}", file, ex.Message);
                }
        }
        catch (Exception ex)
        {
            _log.LogError("Error reading schedules directory: {Message}", ex.Message);
        }

        return schedules.OrderBy(s => s.Name).ToList();
    }

    /// <summary>
    ///     Delete a schedule
    /// </summary>
    public bool DeleteSchedule(string scheduleName)
    {
        try
        {
            var filename = SanitizeFilename(scheduleName) + ".json";
            var filepath = Path.Combine(_schedulesDirectory, filename);

            if (!File.Exists(filepath))
            {
                _log.LogWarning("Schedule file not found: {Filepath}", filepath);
                return false;
            }

            File.Delete(filepath);
            _log.LogInformation("Deleted schedule '{ScheduleName}'", scheduleName);

            // If this was the active schedule, clear it
            if (_activeSchedule?.Name == scheduleName)
            {
                _activeSchedule = null;
                _log.LogInformation("Cleared active schedule");
            }

            return true;
        }
        catch (Exception ex)
        {
            _log.LogError("Error deleting schedule '{ScheduleName}': {Message}", scheduleName, ex.Message);
            return false;
        }
    }

    /// <summary>
    ///     Set a schedule as the default (auto-load on startup)
    /// </summary>
    public bool SetDefaultSchedule(string scheduleName)
    {
        try
        {
            // Clear existing default
            var allSchedules = GetAllSchedules();
            foreach (var schedule in allSchedules.Where(s => s.IsDefault))
            {
                schedule.IsDefault = false;
                SaveSchedule(schedule);
            }

            // Set new default
            var targetSchedule = LoadSchedule(scheduleName);
            if (targetSchedule == null)
                return false;

            targetSchedule.IsDefault = true;
            SaveSchedule(targetSchedule);

            // ✅ FIX: Also activate the schedule at runtime if it's enabled
            if (targetSchedule.Enabled)
            {
                _activeSchedule = targetSchedule;
                _log.LogInformation("Activated default schedule '{ScheduleName}'", scheduleName);
            }

            _log.LogInformation("Set '{ScheduleName}' as default schedule", scheduleName);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError("Error setting default schedule: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    ///     Get the default schedule
    /// </summary>
    public LayoutSchedule? GetDefaultSchedule()
    {
        return GetAllSchedules().FirstOrDefault(s => s.IsDefault);
    }

    /// <summary>
    ///     Activate a specific schedule
    /// </summary>
    public bool ActivateSchedule(string scheduleName)
    {
        var schedule = LoadSchedule(scheduleName);
        if (schedule == null)
        {
            _log.LogWarning("Cannot activate schedule '{ScheduleName}' - not found", scheduleName);
            return false;
        }

        _activeSchedule = schedule;
        _log.LogInformation("Activated schedule '{ScheduleName}' with {Count} entries", scheduleName, schedule.Entries.Count);
        _log.LogInformation("Enabled: {Enabled}, IsDefault: {IsDefault}", schedule.Enabled, schedule.IsDefault);
        return true;
    }

    /// <summary>
    ///     Refresh/reload the active schedule from disk
    /// </summary>
    public void RefreshActiveSchedule()
    {
        // If we have an active schedule, reload it from disk
        if (_activeSchedule != null)
        {
            var reloaded = LoadSchedule(_activeSchedule.Name);
            if (reloaded != null)
            {
                _activeSchedule = reloaded;
                _log.LogInformation("Refreshed active schedule '{Name}'", _activeSchedule.Name);
            }
        }
        else
        {
            // Try to load default schedule
            AutoActivateIfNeeded();
        }
    }

    /// <summary>
    ///     Auto-activate the first enabled schedule if no schedule is currently active
    /// </summary>
    public void AutoActivateIfNeeded()
    {
        // If there's already an active schedule, don't change it
        if (_activeSchedule != null && _activeSchedule.Enabled)
        {
            _log.LogInformation("Active schedule already exists: '{Name}'", _activeSchedule.Name);
            return;
        }

        // Try to find an enabled schedule (prefer default first)
        var defaultSchedule = GetDefaultSchedule();
        if (defaultSchedule != null && defaultSchedule.Enabled)
        {
            _activeSchedule = defaultSchedule;
            _log.LogInformation("Auto-activated default schedule: '{Name}'", _activeSchedule.Name);
            return;
        }

        // Fall back to any enabled schedule
        var enabledSchedules = GetAllSchedules().Where(s => s.Enabled).ToList();
        if (enabledSchedules.Count > 0)
        {
            _activeSchedule = enabledSchedules.First();
            _log.LogInformation("Auto-activated first enabled schedule: '{Name}'", _activeSchedule.Name);
        }
        else
        {
            _log.LogInformation("No enabled schedules found to activate");
        }
    }

    /// <summary>
    ///     Get the currently active schedule
    /// </summary>
    public LayoutSchedule? GetActiveSchedule()
    {
        return _activeSchedule;
    }

    /// <summary>
    ///     Get next scheduled layout change
    /// </summary>
    public (LayoutScheduleEntry? Entry, TimeSpan TimeUntil)? GetNextScheduledChange()
    {
        if (_activeSchedule == null)
        {
            _log.LogDebug("GetNextScheduledChange: No active schedule");
            return null;
        }

        if (!_activeSchedule.Enabled)
        {
            _log.LogInformation("GetNextScheduledChange: Active schedule '{Name}' is disabled", _activeSchedule.Name);
            return null;
        }

        var now = DateTime.Now;
        var currentDay = (int)now.DayOfWeek;
        LayoutScheduleEntry? nextEntry = null;
        DateTime? nextOccurrence = null;

        foreach (var entry in _activeSchedule.Entries.Where(e => e.Enabled))
        {
            // Parse entry time
            if (!TimeSpan.TryParse(entry.Time, out var entryTime))
                continue;

            // Check each day this entry is active on
            var daysToCheck = entry.ActiveDays.Count > 0 ? entry.ActiveDays : Enumerable.Range(0, 7).ToList();

            foreach (var day in daysToCheck)
            {
                var daysUntil = (day - currentDay + 7) % 7;
                var candidateTime = now.Date.AddDays(daysUntil).Add(entryTime);

                // If it's today but already passed, check next week
                if (candidateTime <= now)
                    // If this is the same day of week, try next week
                    candidateTime = candidateTime.AddDays(7);

                // Keep track of the earliest occurrence
                if (!nextOccurrence.HasValue || candidateTime < nextOccurrence.Value)
                {
                    nextOccurrence = candidateTime;
                    nextEntry = entry;
                }
            }
        }

        if (nextEntry != null && nextOccurrence.HasValue)
        {
            var timeUntil = nextOccurrence.Value - now;
            return (nextEntry, timeUntil);
        }

        return null;
    }

    /// <summary>
    ///     Clear the active schedule (called when user manually loads a layout)
    /// </summary>
    public void ClearActiveSchedule()
    {
        if (_activeSchedule != null)
        {
            _log.LogInformation("Clearing active schedule '{Name}' (user override)", _activeSchedule.Name);
            _activeSchedule = null;
        }
    }

    /// <summary>
    ///     Check if any scheduled layout changes should occur
    /// </summary>
    private void CheckSchedules(object? state)
    {
        var now = DateTime.Now;
        var currentTime = now.ToString("HH:mm");
        var currentDay = (int)now.DayOfWeek;

        // ✅ ADD DEBUG LOGGING
        _log.LogInformation("CheckSchedules() fired at {CurrentTime}", currentTime);
        _log.LogInformation("Active schedule: {Value}", (_activeSchedule != null ? $"'{_activeSchedule.Name}' (Enabled: {_activeSchedule.Enabled})" : "NONE"));

        if (_activeSchedule == null || !_activeSchedule.Enabled)
        {
            _log.LogInformation("Skipping check - no active enabled schedule");
            return;
        }

        _log.LogInformation("Checking {Value} enabled entries", _activeSchedule.Entries.Count(e => e.Enabled));

        // Prevent duplicate triggers within the same minute
        if (now.Year == _lastCheckTime.Year &&
            now.Month == _lastCheckTime.Month &&
            now.Day == _lastCheckTime.Day &&
            now.Hour == _lastCheckTime.Hour &&
            now.Minute == _lastCheckTime.Minute)
        {
            _log.LogInformation("Skipping - already checked this minute");
            return;
        }

        _lastCheckTime = now;

        foreach (var entry in _activeSchedule.Entries.Where(e => e.Enabled))
        {
            _log.LogInformation("Entry: '{LayoutName}' at {Time}", entry.LayoutName, entry.Time);

            // Check if time matches
            if (entry.Time != currentTime)
            {
                _log.LogInformation("Time mismatch: '{Time}' != '{CurrentTime}'", entry.Time, currentTime);
                continue;
            }

            // Check if day matches (empty list means all days)
            if (entry.ActiveDays.Count > 0 && !entry.ActiveDays.Contains(currentDay))
            {
                _log.LogInformation("Day mismatch: {CurrentDay} not in [{Value}]", currentDay, string.Join(",", entry.ActiveDays));
                continue;
            }

            // Prevent re-triggering if already triggered today at this time
            if (entry.LastTriggered.HasValue &&
                entry.LastTriggered.Value.Date == now.Date &&
                entry.LastTriggered.Value.Hour == now.Hour &&
                entry.LastTriggered.Value.Minute == now.Minute)
            {
                _log.LogInformation("Already triggered at this time today");
                continue;
            }

            // Trigger the layout change
            _log.LogInformation("✅ TRIGGERED: '{LayoutName}' at {CurrentTime} (Entry: {Description})", entry.LayoutName, currentTime, entry.Description);
            entry.LastTriggered = now;

            // Fire event
            ScheduleTriggered?.Invoke(this, new LayoutScheduleTriggeredEventArgs
            {
                LayoutName = entry.LayoutName,
                ScheduleEntry = entry,
                ScheduleName = _activeSchedule.Name
            });

            // Save updated schedule (with new LastTriggered time)
            SaveSchedule(_activeSchedule);
        }
    }

    private string SanitizeFilename(string filename)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", filename.Split(invalid, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.');
        return string.IsNullOrWhiteSpace(sanitized) ? "unnamed" : sanitized;
    }
}

/// <summary>
///     Event args for when a scheduled layout change is triggered
/// </summary>
public class LayoutScheduleTriggeredEventArgs : EventArgs
{
    public string LayoutName { get; set; } = string.Empty;
    public string ScheduleName { get; set; } = string.Empty;
    public LayoutScheduleEntry? ScheduleEntry { get; set; }
}
