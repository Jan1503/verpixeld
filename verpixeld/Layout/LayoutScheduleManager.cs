using System.Text.Json;
using verpixeld.Configuration;
using verpixeld.Interfaces;
using verpixeld.Services;

namespace verpixeld.Layout;

/// <summary>
///     Manages layout scheduling - automatic layout switching based on time
/// </summary>
public class LayoutScheduleManager : ILayoutScheduleManager
{
    private readonly LayoutStorageManager _layoutStorage;
    private readonly string _schedulesDirectory;
    private LayoutSchedule? _activeSchedule;
    private DateTime _lastCheckTime;
    private Timer? _schedulerTimer;

    public LayoutScheduleManager(LayoutStorageManager layoutStorage)
    {
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
            Console.WriteLine(
                $"[SCHEDULER] Loaded default schedule: '{_activeSchedule.Name}' with {_activeSchedule.Entries.Count} entries");
        }
        else
        {
            Console.WriteLine("[SCHEDULER] No default schedule found");
        }

        // ✅ Calculate delay to next minute boundary for precise timing
        var now = DateTime.Now;
        var nextMinute = now.Date.AddHours(now.Hour).AddMinutes(now.Minute + 1);
        var initialDelay = nextMinute - now;

        Console.WriteLine($"[SCHEDULER] Current time: {now:HH:mm:ss}");
        Console.WriteLine($"[SCHEDULER] Next check at: {nextMinute:HH:mm:ss} (in {initialDelay.TotalSeconds:F1}s)");

        // Start timer: fire at next minute boundary, then every minute
        _schedulerTimer = new Timer(
            CheckSchedules,
            null,
            initialDelay, // ✅ Delay until next minute
            TimeSpan.FromMinutes(1) // Then every minute
        );

        Console.WriteLine("[SCHEDULER] Scheduler started (synchronized to minute boundaries)");
    }

    /// <summary>
    ///     Stop the scheduler service
    /// </summary>
    public void Stop()
    {
        _schedulerTimer?.Dispose();
        _schedulerTimer = null;
        Console.WriteLine("[SCHEDULER] Scheduler stopped");
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

            Console.WriteLine($"[SCHEDULER] Saved schedule '{schedule.Name}' to {filepath}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SCHEDULER] Error saving schedule '{schedule.Name}': {ex.Message}");
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
                Console.WriteLine($"[SCHEDULER] Schedule file not found: {filepath}");
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

            Console.WriteLine($"[SCHEDULER] Loaded schedule '{schedule?.Name}' from {filepath}");
            return schedule;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SCHEDULER] Error loading schedule '{scheduleName}': {ex.Message}");
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
                    Console.WriteLine($"[SCHEDULER] Error loading schedule from {file}: {ex.Message}");
                }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SCHEDULER] Error reading schedules directory: {ex.Message}");
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
                Console.WriteLine($"[SCHEDULER] Schedule file not found: {filepath}");
                return false;
            }

            File.Delete(filepath);
            Console.WriteLine($"[SCHEDULER] Deleted schedule '{scheduleName}'");

            // If this was the active schedule, clear it
            if (_activeSchedule?.Name == scheduleName)
            {
                _activeSchedule = null;
                Console.WriteLine("[SCHEDULER] Cleared active schedule");
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SCHEDULER] Error deleting schedule '{scheduleName}': {ex.Message}");
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
                Console.WriteLine($"[SCHEDULER] Activated default schedule '{scheduleName}'");
            }

            Console.WriteLine($"[SCHEDULER] Set '{scheduleName}' as default schedule");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SCHEDULER] Error setting default schedule: {ex.Message}");
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
            Console.WriteLine($"[SCHEDULER] Cannot activate schedule '{scheduleName}' - not found");
            return false;
        }

        _activeSchedule = schedule;
        Console.WriteLine($"[SCHEDULER] Activated schedule '{scheduleName}' with {schedule.Entries.Count} entries");
        Console.WriteLine($"[SCHEDULER]   Enabled: {schedule.Enabled}, IsDefault: {schedule.IsDefault}");
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
                Console.WriteLine($"[SCHEDULER] Refreshed active schedule '{_activeSchedule.Name}'");
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
            Console.WriteLine($"[SCHEDULER] Active schedule already exists: '{_activeSchedule.Name}'");
            return;
        }

        // Try to find an enabled schedule (prefer default first)
        var defaultSchedule = GetDefaultSchedule();
        if (defaultSchedule != null && defaultSchedule.Enabled)
        {
            _activeSchedule = defaultSchedule;
            Console.WriteLine($"[SCHEDULER] Auto-activated default schedule: '{_activeSchedule.Name}'");
            return;
        }

        // Fall back to any enabled schedule
        var enabledSchedules = GetAllSchedules().Where(s => s.Enabled).ToList();
        if (enabledSchedules.Count > 0)
        {
            _activeSchedule = enabledSchedules.First();
            Console.WriteLine($"[SCHEDULER] Auto-activated first enabled schedule: '{_activeSchedule.Name}'");
        }
        else
        {
            Console.WriteLine("[SCHEDULER] No enabled schedules found to activate");
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
            Console.WriteLine("[SCHEDULER] GetNextScheduledChange: No active schedule");
            return null;
        }

        if (!_activeSchedule.Enabled)
        {
            Console.WriteLine(
                $"[SCHEDULER] GetNextScheduledChange: Active schedule '{_activeSchedule.Name}' is disabled");
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
            Console.WriteLine($"[SCHEDULER] Clearing active schedule '{_activeSchedule.Name}' (user override)");
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
        Console.WriteLine($"[SCHEDULER] CheckSchedules() fired at {currentTime}");
        Console.WriteLine(
            $"[SCHEDULER]   Active schedule: {(_activeSchedule != null ? $"'{_activeSchedule.Name}' (Enabled: {_activeSchedule.Enabled})" : "NONE")}");

        if (_activeSchedule == null || !_activeSchedule.Enabled)
        {
            Console.WriteLine("[SCHEDULER]   Skipping check - no active enabled schedule");
            return;
        }

        Console.WriteLine($"[SCHEDULER]   Checking {_activeSchedule.Entries.Count(e => e.Enabled)} enabled entries");

        // Prevent duplicate triggers within the same minute
        if (now.Year == _lastCheckTime.Year &&
            now.Month == _lastCheckTime.Month &&
            now.Day == _lastCheckTime.Day &&
            now.Hour == _lastCheckTime.Hour &&
            now.Minute == _lastCheckTime.Minute)
        {
            Console.WriteLine("[SCHEDULER]   Skipping - already checked this minute");
            return;
        }

        _lastCheckTime = now;

        foreach (var entry in _activeSchedule.Entries.Where(e => e.Enabled))
        {
            Console.WriteLine($"[SCHEDULER]   Entry: '{entry.LayoutName}' at {entry.Time}");

            // Check if time matches
            if (entry.Time != currentTime)
            {
                Console.WriteLine($"[SCHEDULER]     Time mismatch: '{entry.Time}' != '{currentTime}'");
                continue;
            }

            // Check if day matches (empty list means all days)
            if (entry.ActiveDays.Count > 0 && !entry.ActiveDays.Contains(currentDay))
            {
                Console.WriteLine(
                    $"[SCHEDULER]     Day mismatch: {currentDay} not in [{string.Join(",", entry.ActiveDays)}]");
                continue;
            }

            // Prevent re-triggering if already triggered today at this time
            if (entry.LastTriggered.HasValue &&
                entry.LastTriggered.Value.Date == now.Date &&
                entry.LastTriggered.Value.Hour == now.Hour &&
                entry.LastTriggered.Value.Minute == now.Minute)
            {
                Console.WriteLine("[SCHEDULER]     Already triggered at this time today");
                continue;
            }

            // Trigger the layout change
            Console.WriteLine(
                $"[SCHEDULER] ✅ TRIGGERED: '{entry.LayoutName}' at {currentTime} (Entry: {entry.Description})");
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
