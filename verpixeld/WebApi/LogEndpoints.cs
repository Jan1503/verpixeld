using Microsoft.Extensions.DependencyInjection;
using verpixeld.Services;

namespace verpixeld.WebApi;

/// <summary>
///     API endpoints for streaming console logs to the GUI
/// </summary>
public static class LogEndpoints
{
    public static void MapLogEndpoints(this WebApplication app)
    {
        var logService = app.Services.GetRequiredService<LogService>();

        var group = app.MapGroup("/api/logs");

        // Get recent log entries (initial load)
        group.MapGet("/", (int? count) =>
        {
            var (entries, latestSeq) = logService.GetLatest(count ?? 200);
            return Results.Json(new
            {
                success = true,
                entries = entries.Select(e => new
                {
                    seq = e.Sequence,
                    time = e.Timestamp.ToString("HH:mm:ss.fff"),
                    msg = e.Message
                }),
                latestSequence = latestSeq
            });
        });

        // Poll for new entries since a given sequence
        group.MapGet("/poll", (long since) =>
        {
            var (entries, latestSeq) = logService.GetEntriesSince(since);
            return Results.Json(new
            {
                success = true,
                entries = entries.Select(e => new
                {
                    seq = e.Sequence,
                    time = e.Timestamp.ToString("HH:mm:ss.fff"),
                    msg = e.Message
                }),
                latestSequence = latestSeq
            });
        });
    }
}
