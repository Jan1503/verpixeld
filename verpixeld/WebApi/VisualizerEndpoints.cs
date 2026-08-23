using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using verpixeld.MediaPlayer.Audio;
using verpixeld.Services;

namespace verpixeld.WebApi;

/// <summary>
///     API endpoints for audio visualization control
/// </summary>
public static class VisualizerEndpoints
{
    public static void MapVisualizerEndpoints(this WebApplication app)
    {
        var ctx = app.Services.GetRequiredService<EndpointContext>();
        var visualizerService = app.Services.GetRequiredService<IAudioVisualizerService>();

        var group = app.MapGroup("/api/visualizer").WithTags("Audio Visualizer");

        /// <summary>
        /// Get visualizer status
        /// </summary>
        group.MapGet("/status", () =>
        {
            try
            {
                var status = visualizerService.GetStatus();

                // Get available canvases from layout manager
                var canvases = ctx.LayoutManager.GetAllCanvases()
                    .Where(c => !SystemOverlayCanvases.IsSystem(c.Name))
                    .Select(c => new { id = c.Name, name = c.Name })
                    .ToArray();

                return ApiResponse.Ok(new
                {
                    status.IsRunning,
                    targetCanvasId = status.TargetCanvasName,
                    status.Mode,
                    status.ColorScheme,
                    status.Sensitivity,
                    status.Smoothing,
                    availableCanvases = canvases,
                    modes = Enum.GetNames<AudioVisualizerService.VisualizationMode>(),
                    colorSchemes = Enum.GetNames<AudioVisualizerService.ColorScheme>()
                });
            }
            catch (Exception ex)
            {
                return ApiResponse.Error(ex);
            }
        });

        /// <summary>
        /// Start the visualizer on a canvas
        /// </summary>
        group.MapPost("/start", async ([FromBody] StartVisualizerRequest request) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.CanvasId))
                    return ApiResponse.Fail("Canvas name is required");

                // Get canvas from layout manager
                var canvas = ctx.LayoutManager.GetCanvas(request.CanvasId);
                if (canvas == null)
                    return ApiResponse.Fail($"Canvas '{request.CanvasId}' not found");

                // Apply settings if provided
                if (!string.IsNullOrEmpty(request.Mode) &&
                    Enum.TryParse<AudioVisualizerService.VisualizationMode>(request.Mode, out var mode))
                    visualizerService.Mode = mode;

                if (!string.IsNullOrEmpty(request.ColorScheme) &&
                    Enum.TryParse<AudioVisualizerService.ColorScheme>(request.ColorScheme, out var colorScheme))
                    visualizerService.ColorMode = colorScheme;

                if (request.Sensitivity.HasValue)
                    visualizerService.Sensitivity = Math.Clamp(request.Sensitivity.Value, 0.1, 5.0);

                if (request.Smoothing.HasValue)
                    visualizerService.SmoothingFactor = Math.Clamp(request.Smoothing.Value, 0.0, 0.95);

                var success = await visualizerService.StartAsync(canvas, request.CanvasId);

                return Results.Json(new
                {
                    success,
                    message = success
                        ? $"Visualizer started on canvas '{request.CanvasId}'"
                        : "Failed to start visualizer",
                    canvasName = request.CanvasId
                });
            }
            catch (Exception ex)
            {
                return ApiResponse.Error(ex);
            }
        });

        /// <summary>
        /// Stop the visualizer
        /// </summary>
        group.MapPost("/stop", async () =>
        {
            try
            {
                await visualizerService.StopAsync();
                return ApiResponse.Ok("Visualizer stopped");
            }
            catch (Exception ex)
            {
                return ApiResponse.Error(ex);
            }
        });

        /// <summary>
        /// Update visualizer settings
        /// </summary>
        group.MapPut("/settings", ([FromBody] VisualizerSettingsRequest request) =>
        {
            try
            {
                if (!string.IsNullOrEmpty(request.Mode) &&
                    Enum.TryParse<AudioVisualizerService.VisualizationMode>(request.Mode, out var mode))
                    visualizerService.Mode = mode;

                if (!string.IsNullOrEmpty(request.ColorScheme) &&
                    Enum.TryParse<AudioVisualizerService.ColorScheme>(request.ColorScheme, out var colorScheme))
                    visualizerService.ColorMode = colorScheme;

                if (request.Sensitivity.HasValue)
                    visualizerService.Sensitivity = Math.Clamp(request.Sensitivity.Value, 0.1, 5.0);

                if (request.Smoothing.HasValue)
                    visualizerService.SmoothingFactor = Math.Clamp(request.Smoothing.Value, 0.0, 0.95);

                return ApiResponse.Ok(visualizerService.GetStatus(), "Settings updated");
            }
            catch (Exception ex)
            {
                return ApiResponse.Error(ex);
            }
        });
    }

    public record StartVisualizerRequest(
        string CanvasId,
        string? Mode = null,
        string? ColorScheme = null,
        double? Sensitivity = null,
        double? Smoothing = null
    );

    public record VisualizerSettingsRequest(
        string? Mode = null,
        string? ColorScheme = null,
        double? Sensitivity = null,
        double? Smoothing = null
    );
}
