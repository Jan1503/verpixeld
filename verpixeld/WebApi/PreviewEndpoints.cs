using Microsoft.Extensions.DependencyInjection;
using verpixeld.Services;

namespace verpixeld.WebApi;

/// <summary>
///     API endpoints for the live canvas preview (MJPEG stream + snapshot).
/// </summary>
public static class PreviewEndpoints
{
    public static void MapPreviewEndpoints(this WebApplication app)
    {
        var frameStream = app.Services.GetRequiredService<FrameStreamService>();

        var group = app.MapGroup("/api/preview");

        // ── MJPEG live stream ──────────────────────────────────────────
        // Usage: <img src="/api/preview/stream">
        group.MapGet("/stream", async (HttpContext context) =>
        {
            // Disable response buffering for real-time streaming
            var bufferingFeature = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
            bufferingFeature?.DisableBuffering();

            await frameStream.StreamFramesAsync(context.Response, context.RequestAborted);
        });

        // ── Single frame snapshot (JPEG) ───────────────────────────────
        group.MapGet("/frame", () =>
        {
            var jpeg = frameStream.GetLatestFrame();

            if (jpeg == null)
                return Results.Json(new { success = false, error = "No frame available yet" });

            return Results.File(jpeg, "image/jpeg");
        });

        // ── Preview status ─────────────────────────────────────────────
        group.MapGet("/status", () =>
        {
            return Results.Json(new
            {
                success = true,
                data = new
                {
                    hasFrame = frameStream.GetLatestFrame() != null,
                    clientCount = frameStream.ClientCount,
                    active = frameStream.HasClients
                }
            });
        });
    }
}
