using verpixeld.Layout;

namespace verpixeld.Interfaces;

/// <summary>
///     Manages layout scheduling - automatic layout switching based on time
/// </summary>
public interface ILayoutScheduleManager
{
    /// <summary>
    ///     Event fired when a scheduled layout change occurs
    /// </summary>
    event EventHandler<LayoutScheduleTriggeredEventArgs>? ScheduleTriggered;

    /// <summary>
    ///     Starts the scheduler service
    /// </summary>
    void Start();

    /// <summary>
    ///     Stops the scheduler service
    /// </summary>
    void Stop();

    /// <summary>
    ///     Loads a schedule by name
    /// </summary>
    LayoutSchedule? LoadSchedule(string scheduleName);

    /// <summary>
    ///     Saves a schedule
    /// </summary>
    bool SaveSchedule(LayoutSchedule schedule);

    /// <summary>
    ///     Deletes a schedule
    /// </summary>
    bool DeleteSchedule(string scheduleName);

    /// <summary>
    ///     Sets a schedule as the default
    /// </summary>
    bool SetDefaultSchedule(string scheduleName);

    /// <summary>
    ///     Gets the default schedule
    /// </summary>
    LayoutSchedule? GetDefaultSchedule();

    /// <summary>
    ///     Gets the currently active schedule
    /// </summary>
    LayoutSchedule? GetActiveSchedule();

    /// <summary>
    ///     Activates a schedule by name
    /// </summary>
    bool ActivateSchedule(string scheduleName);

    /// <summary>
    ///     Clears the active schedule
    /// </summary>
    void ClearActiveSchedule();

    /// <summary>
    ///     Auto-activates an appropriate schedule if needed
    /// </summary>
    void AutoActivateIfNeeded();

    /// <summary>
    ///     Refreshes the active schedule from disk
    /// </summary>
    void RefreshActiveSchedule();

    /// <summary>
    ///     Gets all saved schedules
    /// </summary>
    List<LayoutSchedule> GetAllSchedules();

    /// <summary>
    ///     Gets a schedule by name (alias for LoadSchedule)
    /// </summary>
    LayoutSchedule? GetSchedule(string scheduleName);

    /// <summary>
    ///     Gets the next scheduled layout change
    /// </summary>
    (LayoutScheduleEntry? Entry, TimeSpan TimeUntil)? GetNextScheduledChange();
}
