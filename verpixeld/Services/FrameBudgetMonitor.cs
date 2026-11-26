using CanvasManagement;
using CanvasManagement.Interfaces;
using verpixeld.Interfaces;

namespace verpixeld.Services;

/// <summary>One canvas's frame timing, as the diagnostics API returns it.</summary>
public sealed record FrameReport(
    string Canvas,
    string? Extension,
    double Fps,
    double? AvgRenderMs,
    double? MaxRenderMs,
    long Frames,
    bool OverBudget);

/// <summary>
///     Watches how long each canvas's extension needs per frame and warns (at most once a minute per canvas)
///     when it goes over budget. On the Pi a slow extension starves the compositor and the whole wall stutters.
///     Render times exist for extensions on <see cref="ExtensionLoop"/>; the others report fps only.
/// </summary>
[LogTag("PERF")]
public sealed class FrameBudgetMonitor(CanvasManager canvases, ICanvasContentManager contents, ILogger<FrameBudgetMonitor> log) : BackgroundService
{
    private readonly ILogger<FrameBudgetMonitor> _log = log;

    /// <summary>Half a 60 fps frame: what one canvas may spend so several can share the CPU.</summary>
    public const double BudgetMs = 8.0;

    private readonly Dictionary<string, DateTime> _warned = new();

    public IReadOnlyList<FrameReport> Report()
    {
        var list = new List<FrameReport>();
        foreach (var (_, name, _, canvas) in canvases.GetCanvasesWithInfo())
        {
            var s = canvas.Stats.Snapshot();
            var ext = contents.GetContent(name)?.ExtensionDisplayName;
            list.Add(new FrameReport(name, ext, Math.Round(s.Fps, 1), Round(s.AvgRenderMs), Round(s.MaxRenderMs), s.Frames,
                s.AvgRenderMs > BudgetMs));
        }
        return list;
    }

    private static double? Round(double? v) => v is { } x ? Math.Round(x, 2) : null;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                foreach (var r in Report())
                {
                    if (!r.OverBudget) continue;
                    if (_warned.TryGetValue(r.Canvas, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(1)) continue;
                    _warned[r.Canvas] = DateTime.UtcNow;
                    _log.LogWarning("{Source}: {AvgMs:0.0} ms per frame (max {MaxMs:0.0}), budget {BudgetMs} ms",
                        r.Extension ?? r.Canvas, r.AvgRenderMs, r.MaxRenderMs, BudgetMs);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Frame budget check failed");
            }
        }
    }
}
