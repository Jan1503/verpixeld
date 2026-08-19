using System.Text.Json;
using verpixeld.Layout;
using verpixeld.Services;

namespace verpixeld.WebApi;

/// <summary>
///     REST API for the layout (scene) playlist — rotate saved layouts on an interval with transitions.
/// </summary>
public static class PlaylistEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static void MapPlaylistEndpoints(this WebApplication app, LayoutPlaylistService svc)
    {
        app.MapGet("/api/playlist", () =>
            Results.Json(new ApiResponse<object>(true, new
            {
                isRunning = svc.IsRunning,
                currentLayout = svc.CurrentLayout,
                config = svc.Config
            })));

        app.MapPost("/api/playlist/configure", async context =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var cfg = JsonSerializer.Deserialize<PlaylistConfiguration>(body, JsonOpts)
                          ?? new PlaylistConfiguration();
                svc.Configure(cfg);
                await context.Response.WriteAsJsonAsync(new ApiResponse<object>(true,
                    new { isRunning = svc.IsRunning, config = svc.Config }));
            }
            catch (Exception ex)
            {
                await context.Response.WriteAsJsonAsync(new ApiResponse<object>(false, Error: ex.Message));
            }
        });

        app.MapPost("/api/playlist/start", () =>
        {
            svc.Start();
            return Results.Json(new ApiResponse<string>(true, "Playlist started"));
        });

        app.MapPost("/api/playlist/stop", () =>
        {
            svc.Stop();
            return Results.Json(new ApiResponse<string>(true, "Playlist stopped"));
        });

        app.MapPost("/api/playlist/next", () =>
        {
            svc.Next();
            return Results.Json(new ApiResponse<string>(true, "Advanced"));
        });

        app.MapPost("/api/playlist/suspend", () =>
        {
            var wasRunning = svc.Suspend();
            return Results.Json(new ApiResponse<object>(true, new { wasRunning }));
        });

        app.MapPost("/api/playlist/resume", () =>
        {
            svc.Resume();
            return Results.Json(new ApiResponse<string>(true, "Resumed"));
        });

        app.MapPost("/api/playlist/previous", () =>
        {
            svc.Previous();
            return Results.Json(new ApiResponse<string>(true, "Back"));
        });
    }
}
