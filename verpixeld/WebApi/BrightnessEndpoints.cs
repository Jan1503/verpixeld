using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using verpixeld.Services;

namespace verpixeld.WebApi;

/// <summary>
///     Brightness control API endpoints - Global and Per-Canvas
/// </summary>
[LogTag("API")]
public static class BrightnessEndpoints
{
    /// <summary>Set when the endpoints are mapped; handlers in helper methods log through it too.</summary>
    private static ILogger Log = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public static void MapBrightnessEndpoints(this WebApplication app)
    {
        Log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(BrightnessEndpoints));
        var ctx = app.Services.GetRequiredService<EndpointContext>();

        // Get global brightness
        app.MapGet("/api/brightness/global", () =>
        {
            try
            {
                return ApiResponse.Ok(new
                {
                    brightness = ctx.CanvasManager.Brightness,
                    percentage = (int)(ctx.CanvasManager.Brightness * 100)
                });
            }
            catch (Exception ex)
            {
                return ApiResponse.Error(ex);
            }
        });

        // Set global brightness
        app.MapPost("/api/brightness/global", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var jsonDoc = JsonDocument.Parse(body);

                if (!jsonDoc.RootElement.TryGetProperty("brightness", out var brightnessElement))
                    return ApiResponse.Fail("Brightness value is required");

                var brightness = brightnessElement.GetDouble();

                // Clamp between 0.0 and 1.0
                brightness = Math.Max(0.0, Math.Min(1.0, brightness));

                ctx.CanvasManager.Brightness = (float)brightness;
                Log.LogInformation("Global brightness set to {Brightness:F2} ({Value}%)", brightness, (int)(brightness * 100));

                return ApiResponse.Ok(new
                {
                    brightness = ctx.CanvasManager.Brightness,
                    percentage = (int)(ctx.CanvasManager.Brightness * 100)
                });
            }
            catch (Exception ex)
            {
                Log.LogError("Error setting global brightness: {Message}", ex.Message);
                return ApiResponse.Error(ex);
            }
        });

        // Get all canvas brightness levels
        app.MapGet("/api/brightness/canvases", () =>
        {
            try
            {
                var canvases = ctx.LayoutManager.GetAllCanvases()
                    .Where(c => !SystemOverlayCanvases.IsSystem(c.Name))
                    .Select(c => new
                    {
                        name = c.Name,
                        brightness = c.Canvas.Brightness,
                        percentage = (int)(c.Canvas.Brightness * 100)
                    });

                return ApiResponse.Ok(canvases);
            }
            catch (Exception ex)
            {
                return ApiResponse.Error(ex);
            }
        });

        // Get brightness for specific canvas
        app.MapGet("/api/brightness/canvas/{canvasName}", (string canvasName) =>
        {
            try
            {
                var canvas = ctx.LayoutManager.GetCanvas(canvasName);
                if (canvas == null)
                    return ApiResponse.Fail($"Canvas '{canvasName}' not found");

                return ApiResponse.Ok(new
                {
                    canvasName,
                    brightness = canvas.Brightness,
                    percentage = (int)(canvas.Brightness * 100)
                });
            }
            catch (Exception ex)
            {
                return ApiResponse.Error(ex);
            }
        });

        // Set brightness for specific canvas
        app.MapPost("/api/brightness/canvas/{canvasName}", async (string canvasName, HttpContext context) =>
        {
            try
            {
                var canvas = ctx.LayoutManager.GetCanvas(canvasName);
                if (canvas == null)
                    return ApiResponse.Fail($"Canvas '{canvasName}' not found");

                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var jsonDoc = JsonDocument.Parse(body);

                if (!jsonDoc.RootElement.TryGetProperty("brightness", out var brightnessElement))
                    return ApiResponse.Fail("Brightness value is required");

                var brightness = brightnessElement.GetDouble();

                // Clamp between 0.0 and 1.0
                brightness = Math.Max(0.0, Math.Min(1.0, brightness));

                canvas.Brightness = (float)brightness;
                Log.LogInformation("Canvas '{CanvasName}' brightness set to {Brightness:F2} ({Value}%)", canvasName, brightness, (int)(brightness * 100));

                return ApiResponse.Ok(new
                {
                    canvasName,
                    brightness = canvas.Brightness,
                    percentage = (int)(canvas.Brightness * 100)
                });
            }
            catch (Exception ex)
            {
                Log.LogError("Error setting canvas brightness: {Message}", ex.Message);
                return ApiResponse.Error(ex);
            }
        });
    }
}
