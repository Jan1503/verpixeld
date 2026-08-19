using System.Text.Json;
using SkiaSharp;

namespace verpixeld.WebApi;

/// <summary>
///     Drawing and live collaborative drawing API endpoints
/// </summary>
public static class DrawingEndpoints
{
    public static void MapDrawingEndpoints(this WebApplication app, EndpointContext ctx)
    {
        var layoutManager = ctx.LayoutManager;

        // Apply drawing to canvas
        app.MapPost("/api/draw/apply/{canvasName}", async (HttpContext context, string canvasName) =>
        {
            try
            {
                if (layoutManager == null)
                    return Results.Json(new ApiResponse<string>(false, Error: "Layout manager not initialized"));

                var canvas = layoutManager.GetCanvas(canvasName);
                if (canvas == null)
                    return Results.Json(new ApiResponse<string>(false, Error: $"Canvas '{canvasName}' not found"));

                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var jsonDoc = JsonDocument.Parse(body);
                var root = jsonDoc.RootElement;

                if (!root.TryGetProperty("imageData", out var imageDataElement))
                    return Results.Json(new ApiResponse<string>(false, Error: "Missing imageData property"));

                var imageDataUrl = imageDataElement.GetString();
                if (string.IsNullOrEmpty(imageDataUrl))
                    return Results.Json(new ApiResponse<string>(false, Error: "Image data is empty"));

                var base64Data = imageDataUrl.Contains(",") ? imageDataUrl.Split(',')[1] : imageDataUrl;
                var imageBytes = Convert.FromBase64String(base64Data);

                using var bitmap = SKBitmap.Decode(imageBytes);
                if (bitmap == null)
                    return Results.Json(new ApiResponse<string>(false, Error: "Failed to decode image"));

                canvas.DrawBitmap(bitmap, 0, 0, bitmap.Width, bitmap.Height);

                return Results.Json(new ApiResponse<string>(true, $"Drawing applied to canvas '{canvasName}'"));
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResponse<string>(false, Error: ex.Message));
            }
        });

        // SSE endpoint for live drawing events
        app.MapGet("/api/draw/live/events", async context =>
        {
            context.Response.Headers["Content-Type"] = "text/event-stream";
            context.Response.Headers["Cache-Control"] = "no-cache";
            context.Response.Headers["Connection"] = "keep-alive";

            var clientId = Guid.NewGuid().ToString();
            LiveDrawingBroadcast.AddClient(clientId, context.Response);

            try
            {
                await context.Response.WriteAsync($"event: connected\ndata: {{\"clientId\":\"{clientId}\"}}\n\n");
                await context.Response.Body.FlushAsync();

                while (!context.RequestAborted.IsCancellationRequested)
                {
                    await Task.Delay(30000, context.RequestAborted);
                    await context.Response.WriteAsync(": keepalive\n\n");
                    await context.Response.Body.FlushAsync();
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                LiveDrawingBroadcast.RemoveClient(clientId);
            }
        });

        // Live draw strokes
        app.MapPost("/api/draw/live/{canvasName}", async (HttpContext context, string canvasName) =>
        {
            try
            {
                if (layoutManager == null)
                    return Results.Json(new ApiResponse<string>(false, Error: "Layout manager not initialized"));

                var canvas = layoutManager.GetCanvas(canvasName);
                if (canvas == null)
                    return Results.Json(new ApiResponse<string>(false, Error: $"Canvas '{canvasName}' not found"));

                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var jsonDoc = JsonDocument.Parse(body);
                var root = jsonDoc.RootElement;

                if (!root.TryGetProperty("strokes", out var strokesElement))
                    return Results.Json(new ApiResponse<string>(false, Error: "Missing strokes property"));

                var senderId = root.TryGetProperty("clientId", out var clientIdEl) ? clientIdEl.GetString() : null;
                var strokesForBroadcast = new List<object>();

                foreach (var stroke in strokesElement.EnumerateArray())
                {
                    var x1 = stroke.GetProperty("x1").GetInt32();
                    var y1 = stroke.GetProperty("y1").GetInt32();
                    var x2 = stroke.GetProperty("x2").GetInt32();
                    var y2 = stroke.GetProperty("y2").GetInt32();
                    var colorHex = stroke.GetProperty("color").GetString() ?? "#FFFFFF";
                    var alpha = stroke.TryGetProperty("alpha", out var alphaEl) ? (float)alphaEl.GetDouble() : 1.0f;
                    var size = stroke.TryGetProperty("size", out var sizeEl) ? sizeEl.GetInt32() : 1;

                    var color = SKColor.Parse(colorHex).WithAlpha((byte)(alpha * 255));
                    EndpointHelpers.DrawLineOnCanvas(canvas, x1, y1, x2, y2, color, size);
                    strokesForBroadcast.Add(new { x1, y1, x2, y2, color = colorHex, alpha, size });
                }

                _ = LiveDrawingBroadcast.BroadcastStrokes(canvasName, strokesForBroadcast, senderId);
                return Results.Json(new ApiResponse<string>(true));
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResponse<string>(false, Error: ex.Message));
            }
        });

        // Clear canvas
        app.MapPost("/api/draw/live/clear/{canvasName}", async (HttpContext context, string canvasName) =>
        {
            try
            {
                if (layoutManager == null)
                    return Results.Json(new ApiResponse<string>(false, Error: "Layout manager not initialized"));

                var canvas = layoutManager.GetCanvas(canvasName);
                if (canvas == null)
                    return Results.Json(new ApiResponse<string>(false, Error: $"Canvas '{canvasName}' not found"));

                string? senderId = null;
                try
                {
                    using var reader = new StreamReader(context.Request.Body);
                    var body = await reader.ReadToEndAsync();
                    if (!string.IsNullOrEmpty(body))
                    {
                        var jsonDoc = JsonDocument.Parse(body);
                        senderId = jsonDoc.RootElement.TryGetProperty("clientId", out var clientIdEl)
                            ? clientIdEl.GetString()
                            : null;
                    }
                }
                catch
                {
                }

                canvas.Clear(SKColors.Black);
                _ = LiveDrawingBroadcast.BroadcastClear(canvasName, senderId);

                return Results.Json(new ApiResponse<string>(true, $"Canvas '{canvasName}' cleared"));
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResponse<string>(false, Error: ex.Message));
            }
        });

        // Live draw shape
        app.MapPost("/api/draw/live/shape/{canvasName}", async (HttpContext context, string canvasName) =>
        {
            try
            {
                if (layoutManager == null)
                    return Results.Json(new ApiResponse<string>(false, Error: "Layout manager not initialized"));

                var canvas = layoutManager.GetCanvas(canvasName);
                if (canvas == null)
                    return Results.Json(new ApiResponse<string>(false, Error: $"Canvas '{canvasName}' not found"));

                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var jsonDoc = JsonDocument.Parse(body);
                var root = jsonDoc.RootElement;

                var tool = root.GetProperty("tool").GetString() ?? "line";
                var x1 = root.GetProperty("x1").GetInt32();
                var y1 = root.GetProperty("y1").GetInt32();
                var x2 = root.GetProperty("x2").GetInt32();
                var y2 = root.GetProperty("y2").GetInt32();
                var colorHex = root.GetProperty("color").GetString() ?? "#FFFFFF";
                var alpha = root.TryGetProperty("alpha", out var alphaEl) ? (float)alphaEl.GetDouble() : 1.0f;
                var size = root.TryGetProperty("size", out var sizeEl) ? sizeEl.GetInt32() : 1;
                var filled = root.TryGetProperty("filled", out var filledEl) && filledEl.GetBoolean();
                var senderId = root.TryGetProperty("clientId", out var clientIdEl) ? clientIdEl.GetString() : null;

                var color = SKColor.Parse(colorHex).WithAlpha((byte)(alpha * 255));

                switch (tool)
                {
                    case "line":
                        EndpointHelpers.DrawLineOnCanvas(canvas, x1, y1, x2, y2, color, size);
                        break;
                    case "rect":
                        EndpointHelpers.DrawRectOnCanvas(canvas, x1, y1, x2, y2, color, size, filled);
                        break;
                    case "ellipse":
                        EndpointHelpers.DrawEllipseOnCanvas(canvas, x1, y1, x2, y2, color, size, filled);
                        break;
                }

                _ = LiveDrawingBroadcast.BroadcastShape(canvasName, tool, x1, y1, x2, y2, colorHex, alpha, size, filled,
                    senderId);
                return Results.Json(new ApiResponse<string>(true));
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResponse<string>(false, Error: ex.Message));
            }
        });

        // Get all saved drawings
        app.MapGet("/api/drawings", () =>
        {
            try
            {
                var drawings = SharedDrawingsStorage.GetAll();
                return Results.Json(new ApiResponse<List<SavedDrawing>>(true, drawings));
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResponse<List<SavedDrawing>>(false, Error: ex.Message));
            }
        });

        // Save a new drawing
        app.MapPost("/api/drawings", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var drawing = JsonSerializer.Deserialize<SavedDrawing>(body, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (drawing == null)
                    return Results.Json(new ApiResponse<SavedDrawing>(false, Error: "Invalid drawing data"));

                drawing.Id = Guid.NewGuid().ToString();
                drawing.CreatedAt = DateTime.UtcNow.ToString("o");

                SharedDrawingsStorage.Add(drawing);
                return Results.Json(new ApiResponse<SavedDrawing>(true, drawing));
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResponse<SavedDrawing>(false, Error: ex.Message));
            }
        });

        // Delete a drawing
        app.MapDelete("/api/drawings/{id}", (string id) =>
        {
            try
            {
                var success = SharedDrawingsStorage.Delete(id);
                return success
                    ? Results.Json(new ApiResponse<string>(true, "Drawing deleted"))
                    : Results.Json(new ApiResponse<string>(false, Error: "Drawing not found"));
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResponse<string>(false, Error: ex.Message));
            }
        });

        // Delete all drawings
        app.MapDelete("/api/drawings", () =>
        {
            try
            {
                SharedDrawingsStorage.Clear();
                return Results.Json(new ApiResponse<string>(true, "All drawings deleted"));
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResponse<string>(false, Error: ex.Message));
            }
        });
    }
}
