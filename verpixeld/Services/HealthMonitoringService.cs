using System.Diagnostics;

namespace verpixeld.Services;

/// <summary>
///     Background service that monitors system health and memory usage
/// </summary>
public class HealthMonitoringService : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly TimeSpan _interval;
    private readonly long _memoryWarningThresholdMb;
    private Task? _monitorTask;

    public HealthMonitoringService(TimeSpan? interval = null, long memoryWarningThresholdMb = 50)
    {
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
        Console.WriteLine("[HEALTH] Monitoring started");
    }

    /// <summary>
    ///     Stops the health monitoring
    /// </summary>
    public void Stop()
    {
        _cts.Cancel();
        Console.WriteLine("[HEALTH] Monitoring stopped");
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

                Console.WriteLine(
                    $"[HEALTH] Uptime: {uptime.TotalMinutes:F1}min | " +
                    $"Memory: {LastMemoryMb}MB (Δ{memoryDeltaMb:+0;-0}MB) | " +
                    $"Threads: {Process.GetCurrentProcess().Threads.Count}");

                // Warning if memory increasing rapidly
                if (memoryDeltaMb > _memoryWarningThresholdMb)
                {
                    Console.WriteLine($"[HEALTH] WARNING: Memory increased by {memoryDeltaMb}MB - possible leak!");

                    GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                    var afterGc = GC.GetTotalMemory(true) / 1024 / 1024;
                    Console.WriteLine($"[HEALTH] Forced GC, memory now: {afterGc}MB");
                }

                lastMemory = currentMemory;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HEALTH] Monitor error: {ex.Message}");
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
