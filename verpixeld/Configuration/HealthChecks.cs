using System.Diagnostics;
using CanvasManagement;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace verpixeld.Configuration;

/// <summary>
///     Health check for the display/canvas system
/// </summary>
public class DisplayHealthCheck : IHealthCheck
{
    private readonly CanvasManager _canvasManager;

    public DisplayHealthCheck(CanvasManager canvasManager)
    {
        _canvasManager = canvasManager;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var canvasCount = _canvasManager.GetCanvases.Count;
            var filterCount = _canvasManager.GetFilterCount();

            var data = new Dictionary<string, object>
            {
                { "canvasCount", canvasCount },
                { "filterCount", filterCount },
                { "memoryMB", GC.GetTotalMemory(false) / 1024 / 1024 }
            };

            if (canvasCount > 0)
                return Task.FromResult(new HealthCheckResult(
                    HealthStatus.Healthy,
                    "Display system is running",
                    data: data));

            return Task.FromResult(new HealthCheckResult(
                HealthStatus.Degraded,
                "Display running but no canvases",
                data: data));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new HealthCheckResult(
                HealthStatus.Unhealthy,
                $"Health check failed: {ex.Message}",
                ex));
        }
    }
}

/// <summary>
///     Health check for system resources
/// </summary>
public class SystemHealthCheck : IHealthCheck
{
    private const long MemoryWarningThresholdMB = 500;
    private const long MemoryCriticalThresholdMB = 800;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var process = Process.GetCurrentProcess();
            var memoryMB = process.WorkingSet64 / 1024 / 1024;
            var gcMemoryMB = GC.GetTotalMemory(false) / 1024 / 1024;
            var threadCount = process.Threads.Count;
            var uptime = DateTime.UtcNow - process.StartTime.ToUniversalTime();

            var data = new Dictionary<string, object>
            {
                { "workingSetMB", memoryMB },
                { "gcMemoryMB", gcMemoryMB },
                { "threadCount", threadCount },
                { "uptimeMinutes", (int)uptime.TotalMinutes },
                { "uptimeFormatted", $"{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m" }
            };

            if (memoryMB > MemoryCriticalThresholdMB)
                return Task.FromResult(new HealthCheckResult(
                    HealthStatus.Unhealthy,
                    $"Memory usage critical: {memoryMB}MB",
                    data: data));

            if (memoryMB > MemoryWarningThresholdMB)
                return Task.FromResult(new HealthCheckResult(
                    HealthStatus.Degraded,
                    $"Memory usage elevated: {memoryMB}MB",
                    data: data));

            return Task.FromResult(new HealthCheckResult(
                HealthStatus.Healthy,
                $"System healthy, memory: {memoryMB}MB",
                data: data));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new HealthCheckResult(
                HealthStatus.Unhealthy,
                $"Health check failed: {ex.Message}",
                ex));
        }
    }
}

/// <summary>
///     Extension methods for health check registration
/// </summary>
public static class HealthCheckExtensions
{
    public static IServiceCollection AddDisplayHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<DisplayHealthCheck>("display", tags: new[] { "display", "canvas" })
            .AddCheck<SystemHealthCheck>("system", tags: new[] { "system", "memory" });

        return services;
    }
}
