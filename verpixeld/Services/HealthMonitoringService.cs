using System.Diagnostics;

namespace verpixeld.Services;

/// <summary>
///     Background service that monitors system health and memory usage
/// </summary>
[LogTag("HEALTH")]
public class HealthMonitoringService : IDisposable
{
    private readonly ILogger<HealthMonitoringService> _log;

    private readonly CancellationTokenSource _cts = new();
    private readonly TimeSpan _interval;
    private readonly long _memoryWarningThresholdMb;
    private Task? _monitorTask;

    public HealthMonitoringService(TimeSpan? interval = null, long memoryWarningThresholdMb = 50, ILogger<HealthMonitoringService>? log = null)
    {
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<HealthMonitoringService>.Instance;
        _interval = interval ?? TimeSpan.FromSeconds(10);
        _memoryWarningThresholdMb = memoryWarningThresholdMb;
    }

    public bool IsRunning => _monitorTask != null && !_monitorTask.IsCompleted;
    public DateTime StartTime { get; private set; }
    public long LastMemoryMb { get; private set; }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    /// <summary>
    ///     Starts the health monitoring background task
    /// </summary>
    public void Start()
    {
        if (_monitorTask != null)
            return;

        StartTime = DateTime.UtcNow;
        LastMemoryMb = GC.GetTotalMemory(false) / 1024 / 1024;

        _monitorTask = Task.Run(MonitorLoopAsync, _cts.Token);
        _log.LogInformation("Monitoring started");
    }

    /// <summary>
    ///     Stops the health monitoring
    /// </summary>
    public void Stop()
    {
        _cts.Cancel();
        _log.LogInformation("Monitoring stopped");
    }

    private async Task MonitorLoopAsync()
    {
        var lastMemory = GC.GetTotalMemory(false);

        while (!_cts.Token.IsCancellationRequested)
            try
            {
                await Task.Delay(_interval, _cts.Token);

                var currentMemory = GC.GetTotalMemory(false);
                var memoryDeltaMb = (currentMemory - lastMemory) / 1024 / 1024;
                var uptime = DateTime.UtcNow - StartTime;

                LastMemoryMb = currentMemory / 1024 / 1024;

                _log.LogInformation("Uptime: {UptimeMin:F1}min | Memory: {MemoryMb}MB (Δ{DeltaMb:+0;-0}MB) | Threads: {Threads}",
                    uptime.TotalMinutes, LastMemoryMb, memoryDeltaMb, Process.GetCurrentProcess().Threads.Count);

                // Warning if memory increasing rapidly
                if (memoryDeltaMb > _memoryWarningThresholdMb)
                {
                    // Only report: a forced, compacting GC here stalled the render loop and hid real leaks.
                    // POST /api/gc/run collects on demand when investigating.
                    _log.LogWarning("Memory increased by {MemoryDeltaMb}MB - possible leak!", memoryDeltaMb);
                }

                lastMemory = currentMemory;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError("Monitor error: {Message}", ex.Message);
            }
    }

    /// <summary>
    ///     Gets current health status
    /// </summary>
    public HealthStatus GetStatus()
    {
        var memory = GC.GetTotalMemory(false) / 1024 / 1024;
        var uptime = DateTime.UtcNow - StartTime;
        var threads = Process.GetCurrentProcess().Threads.Count;

        return new HealthStatus
        {
            UptimeMinutes = uptime.TotalMinutes,
            MemoryMb = memory,
            ThreadCount = threads,
            IsHealthy = memory < 500 // Consider unhealthy if > 500MB
        };
    }
}

public class HealthStatus
{
    public double UptimeMinutes { get; init; }
    public long MemoryMb { get; init; }
    public int ThreadCount { get; init; }
    public bool IsHealthy { get; init; }
}
