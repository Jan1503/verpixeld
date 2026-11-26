using verpixeld.Services;

namespace verpixeld.WebApi;

/// <summary>Runtime diagnostics for tuning: frame timing per canvas.</summary>
public static class DiagnosticsEndpoints
{
    public static void MapDiagnosticsEndpoints(this WebApplication app)
    {
        var monitor = app.Services.GetRequiredService<FrameBudgetMonitor>();

        // Fps and render time per canvas; "overBudget" when an extension needs more than FrameBudgetMonitor.BudgetMs.
        app.MapGet("/api/diagnostics/frames", () => ApiResponse.Ok(new
        {
            budgetMs = FrameBudgetMonitor.BudgetMs,
            canvases = monitor.Report()
        }));
    }
}
