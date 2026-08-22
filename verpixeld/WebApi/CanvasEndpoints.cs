using System.Text.Json;
using CanvasManagement;
using verpixeld.MediaPlayer;

namespace verpixeld.WebApi;

/// <summary>
///     Canvas stacking and management API endpoints
/// </summary>
public static class CanvasEndpoints
{
    public static void MapCanvasEndpoints(this WebApplication app, EndpointContext ctx,
        MediaPlayerService? mediaService = null)
    {
        var canvasManager = ctx.CanvasManager;
        var layoutManager = ctx.LayoutManager;
        var contentManager = ctx.ContentManager;

        // Resolve a canvas by name. The layout manager only tracks canvases it created; live overlays added
        // directly on the CanvasManager (e.g. the MediaPlayer video overlay) aren't in its dictionary, so fall
        // back to the manager's authoritative list — otherwise opacity/z-order edits report "not found".
        Canvas? ResolveCanvas(string name) =>
            layoutManager?.GetCanvas(name) ?? canvasManager.GetCanvasByName(name);

        // Get all canvases with z-order and opacity info
        app.MapGet("/api/canvas/stack", () =>
        {
            try
            {
                if (layoutManager == null)
                    return Results.Json(new ApiResponse<object[]>(false, Error: "Layout manager not initialized"));

                var canvases = canvasManager.GetCanvasesByZOrder()
                    .Select(c => new
                    {
                        name = c.Name,
                        x = c.XPos,
                        y = c.YPos,
                        width = c.Width,
                        height = c.Height,
                        zOrder = c.ZOrder,
                        opacity = c.Opacity,
                        panelColorBits = c.PanelColorBits,
                        isVisible = !c.IsHidden,
                        transparentBackground = c.TransparentBackground
                    })
                    .ToArray();

                return Results.Json(new ApiResponse<object[]>(true, canvases));
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResponse<object[]>(false, Error: ex.Message));
            }
        });

        // Authoritative physical display size (the matrix), independent of any (possibly stale, oversized)
        // overlay canvases left over from a saved layout made for a different display.
        app.MapGet("/api/canvas/display", () =>
        {
            try
            {
                return Results.Json(new ApiResponse<object>(true,
                    new { width = canvasManager.Width, height = canvasManager.Height }));
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResponse<object>(false, Error: ex.Message));
            }
        });

        // Update canvas z-order
        app.MapPut("/api/canvas/{canvasName}/zorder", async (string canvasName, HttpContext context) =>
        {
            try
            {
                if (layoutManager == null)
                    return Results.Json(new ApiResponse<string>(false, Error: "Layout manager not initialized"));

                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var jsonDoc = JsonDocument.Parse(body);
                var zOrder = jsonDoc.RootElement.GetProperty("zOrder").GetInt32();

                var canvas = ResolveCanvas(canvasName);
                if (canvas == null)
                    return Results.Json(new ApiResponse<string>(false, Error: $"Canvas '{canvasName}' not found"));

                canvasManager.SetCanvasZOrder(canvas, zOrder);
                return Results.Json(new ApiResponse<string>(true, $"Z-order updated to {zOrder}"));
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResponse<string>(false, Error: ex.Message));
            }
        });

        // Update canvas opacity
        app.MapPut("/api/canvas/{canvasName}/opacity", async (string canvasName, HttpContext context) =>
        {
            try
            {
                if (layoutManager == null)
                    return Results.Json(new ApiResponse<string>(false, Error: "Layout manager not initialized"));

                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var jsonDoc = JsonDocument.Parse(body);
                var opacity = Math.Clamp((float)jsonDoc.RootElement.GetProperty("opacity").GetDouble(), 0.0f, 1.0f);

                var canvas = ResolveCanvas(canvasName);
                if (canvas == null)
                    return Results.Json(new ApiResponse<string>(false, Error: $"Canvas '{canvasName}' not found"));

                canvas.Opacity = opacity;
                return Results.Json(new ApiResponse<string>(true, $"Opacity updated to {opacity:P0}"));
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResponse<string>(false, Error: ex.Message));
            }
        });

        // Preferred panel colour depth for this canvas (network output only; 8 or 14).
        app.MapPut("/api/canvas/{canvasName}/colorbits", async (string canvasName, HttpContext context) =>
        {
            try
            {
                if (layoutManager == null)
                    return Results.Json(new ApiResponse<string>(false, Error: "Layout manager not initialized"));

                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var jsonDoc = JsonDocument.Parse(body);
                var bits = jsonDoc.RootElement.GetProperty("panelColorBits").GetInt32();

                var canvas = ResolveCanvas(canvasName);
                if (canvas == null)
                    return Results.Json(new ApiResponse<string>(false, Error: $"Canvas '{canvasName}' not found"));

                canvas.PanelColorBits = bits;
                return Results.Json(new ApiResponse<string>(true,
                    $"Panel colour depth set to {canvas.PanelColorBits}-bit"));
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResponse<string>(false, Error: ex.Message));
            }
        });

        // Update canvas position/size (drag-and-drop). A pure move repositions live (no restart); a size
        // change recreates the canvas and rebuilds its extension at the new dimensions.
        app.MapPut("/api/canvas/{canvasName}/bounds", async (string canvasName, HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var root = JsonDocument.Parse(body).RootElement;

                var canvas = ResolveCanvas(canvasName);
                if (canvas == null)
                    return Results.Json(new ApiResponse<object>(false, Error: $"Canvas '{canvasName}' not found"));

                var dispW = canvasManager.Width;
                var dispH = canvasManager.Height;

                var x = root.TryGetProperty("x", out var xe) ? xe.GetInt32() : canvas.XPos;
                var y = root.TryGetProperty("y", out var ye) ? ye.GetInt32() : canvas.YPos;
                var width = root.TryGetProperty("width", out var we) ? we.GetInt32() : canvas.Width;
                var height = root.TryGetProperty("height", out var he) ? he.GetInt32() : canvas.Height;

                // Keep the canvas fully on-screen.
                width = Math.Clamp(width, 1, dispW);
                height = Math.Clamp(height, 1, dispH);
                x = Math.Clamp(x, 0, dispW - width);
                y = Math.Clamp(y, 0, dispH - height);

                var sizeChanged = width != canvas.Width || height != canvas.Height;

                if (sizeChanged)
                {
                    if (contentManager == null)
                        return Results.Json(new ApiResponse<object>(false, Error: "Content manager not available"));

                    if (!contentManager.ResizeCanvas(canvasName, x, y, width, height))
                        return Results.Json(new ApiResponse<object>(false,
                            Error: $"Canvas '{canvasName}' cannot be resized"));
                }
                else
                {
                    canvasManager.MoveCanvas(canvas, x, y);
                }

                return Results.Json(new ApiResponse<object>(true, new { x, y, width, height }));
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResponse<object>(false, Error: ex.Message));
            }
        });

        // Toggle visibility (does not stop the extension; hidden canvases skip the wall composite).
        app.MapPut("/api/canvas/{canvasName}/visible", async (string canvasName, HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var visible = JsonDocument.Parse(body).RootElement
                    .TryGetProperty("visible", out var v) && v.GetBoolean();

                var canvas = ResolveCanvas(canvasName);
                if (canvas == null)
                    return Results.Json(new ApiResponse<string>(false, Error: $"Canvas '{canvasName}' not found"));

                if (visible) canvas.Show();
                else canvas.Hide();
                return Results.Json(new ApiResponse<string>(true, visible ? "Canvas shown" : "Canvas hidden"));
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResponse<string>(false, Error: ex.Message));
            }
        });

        // Toggle transparent-background (alpha) compositing for a canvas.
        app.MapPut("/api/canvas/{canvasName}/transparent", async (string canvasName, HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var transparent = JsonDocument.Parse(body).RootElement
                    .TryGetProperty("transparent", out var t) && t.GetBoolean();

                var canvas = ResolveCanvas(canvasName);
                if (canvas == null)
                    return Results.Json(new ApiResponse<string>(false, Error: $"Canvas '{canvasName}' not found"));

                canvas.TransparentBackground = transparent;
                return Results.Json(new ApiResponse<string>(true,
                    transparent ? "Transparent background on" : "Transparent background off"));
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResponse<string>(false, Error: ex.Message));
            }
        });

        // Rename an overlay canvas
        app.MapPut("/api/canvas/{canvasName}/rename", async (string canvasName, HttpContext context) =>
        {
            try
            {
                if (layoutManager == null)
                    return Results.Json(new ApiResponse<string>(false, Error: "Layout manager not initialized"));

                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var newName = JsonDocument.Parse(body).RootElement.TryGetProperty("newName", out var n)
                    ? n.GetString()?.Trim()
                    : null;

                if (string.IsNullOrWhiteSpace(newName))
                    return Results.Json(new ApiResponse<string>(false, Error: "New name is required"));

                var standard = new[]
                {
                    "Main", "Header", "Content", "Footer", "Left", "Right",
                    "TopLeft", "TopRight", "BottomLeft", "BottomRight"
                };
                if (standard.Contains(canvasName))
                    return Results.Json(new ApiResponse<string>(false,
                        Error: $"Cannot rename base canvas '{canvasName}'"));
                if (standard.Contains(newName) || layoutManager.HasCanvas(newName))
                    return Results.Json(new ApiResponse<string>(false, Error: $"Name '{newName}' is already in use"));

                if (!layoutManager.RenameCanvas(canvasName, newName))
                    return Results.Json(new ApiResponse<string>(false, Error: $"Canvas '{canvasName}' not found"));

                contentManager?.RenameCanvasContent(canvasName, newName);
                return Results.Json(new ApiResponse<string>(true, $"Renamed to '{newName}'"));
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResponse<string>(false, Error: ex.Message));
            }
        });

        // Move canvas up
        app.MapPost("/api/canvas/{canvasName}/move-up", (string canvasName) =>
        {
            try
            {
                if (layoutManager == null)
                    return Results.Json(new ApiResponse<string>(false, Error: "Layout manager not initialized"));

                var canvas = ResolveCanvas(canvasName);
                if (canvas == null)
                    return Results.Json(new ApiResponse<string>(false, Error: $"Canvas '{canvasName}' not found"));

                canvasManager.MoveUp(canvas);
                return Results.Json(new ApiResponse<string>(true, $"Canvas moved up to z-order {canvas.ZOrder}"));
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResponse<string>(false, Error: ex.Message));
            }
        });

        // Move canvas down
        app.MapPost("/api/canvas/{canvasName}/move-down", (string canvasName) =>
        {
            try
            {
                if (layoutManager == null)
                    return Results.Json(new ApiResponse<string>(false, Error: "Layout manager not initialized"));

                var canvas = ResolveCanvas(canvasName);
                if (canvas == null)
                    return Results.Json(new ApiResponse<string>(false, Error: $"Canvas '{canvasName}' not found"));

                canvasManager.MoveDown(canvas);
                return Results.Json(new ApiResponse<string>(true, $"Canvas moved down to z-order {canvas.ZOrder}"));
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResponse<string>(false, Error: ex.Message));
            }
        });

        // Bring to front
        app.MapPost("/api/canvas/{canvasName}/bring-to-front", (string canvasName) =>
        {
            try
            {
                if (layoutManager == null)
                    return Results.Json(new ApiResponse<string>(false, Error: "Layout manager not initialized"));

                var canvas = ResolveCanvas(canvasName);
                if (canvas == null)
                    return Results.Json(new ApiResponse<string>(false, Error: $"Canvas '{canvasName}' not found"));

                canvasManager.BringToFront(canvas);
                return Results.Json(new ApiResponse<string>(true, "Canvas brought to front"));
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResponse<string>(false, Error: ex.Message));
            }
        });

        // Send to back
        app.MapPost("/api/canvas/{canvasName}/send-to-back", (string canvasName) =>
        {
            try
            {
                if (layoutManager == null)
                    return Results.Json(new ApiResponse<string>(false, Error: "Layout manager not initialized"));

                var canvas = ResolveCanvas(canvasName);
                if (canvas == null)
                    return Results.Json(new ApiResponse<string>(false, Error: $"Canvas '{canvasName}' not found"));

                canvasManager.SendToBack(canvas);
                return Results.Json(new ApiResponse<string>(true, "Canvas sent to back"));
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResponse<string>(false, Error: ex.Message));
            }
        });

        // Remove overlay canvas
        app.MapPost("/api/canvas/{canvasName}/remove", async (string canvasName) =>
        {
            try
            {
                if (layoutManager == null)
                    return Results.Json(new ApiResponse<string>(false, Error: "Layout manager not initialized"));

                var standardCanvases = new[]
                {
                    "Main", "Header", "Content", "Footer", "Left", "Right",
                    "TopLeft", "TopRight", "BottomLeft", "BottomRight"
                };

                if (standardCanvases.Contains(canvasName))
                    return Results.Json(new ApiResponse<string>(false,
                        Error: $"Cannot remove standard canvas '{canvasName}'"));

                var canvas = ResolveCanvas(canvasName);
                if (canvas == null)
                    return Results.Json(new ApiResponse<string>(false, Error: $"Canvas '{canvasName}' not found"));

                // Stop media playback first if it's targeting this canvas (MediaPlayer overlay
                // is created on CanvasManager, not via the layout manager).
                if (mediaService != null)
                    try
                    {
                        await mediaService.NotifyCanvasRemovedAsync(canvasName);
                        if (string.Equals(canvasName, "MediaPlayer", StringComparison.OrdinalIgnoreCase))
                            await mediaService.StopAsync();
                        await Task.Delay(100);
                    }
                    catch
                    {
                    }

                // Drop any per-canvas rotation so its timer can't keep firing on the removed canvas.
                ctx.RotationService?.Forget(canvasName);

                // Stop content (extensions)
                if (contentManager != null)
                    try
                    {
                        contentManager.StopContent(canvasName);
                        Thread.Sleep(100);
                    }
                    catch
                    {
                    }

                layoutManager.RemoveCustomCanvas(canvasName);
                canvas.Hide();
                canvas.Clear();
                Thread.Sleep(50);
                canvasManager.RemoveCanvas(canvas);

                GC.Collect();
                GC.WaitForPendingFinalizers();

                return Results.Json(new ApiResponse<string>(true, $"Canvas '{canvasName}' removed"));
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResponse<string>(false, Error: ex.Message));
            }
        });

        // Create custom canvas
        app.MapPost("/api/canvas/create", async (HttpContext context) =>
        {
            try
            {
                if (layoutManager == null)
                    return Results.Json(new ApiResponse<string>(false, Error: "Layout manager not initialized"));

                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var jsonDoc = JsonDocument.Parse(body);
                var root = jsonDoc.RootElement;

                var name = root.GetProperty("name").GetString() ?? "CustomCanvas";
                var x = root.GetProperty("x").GetInt32();
                var y = root.GetProperty("y").GetInt32();
                var width = root.GetProperty("width").GetInt32();
                var height = root.GetProperty("height").GetInt32();
                var zOrder = root.GetProperty("zOrder").GetInt32();
                var opacity = 1.0f;
                if (root.TryGetProperty("opacity", out var opacityElement))
                    opacity = Math.Clamp((float)opacityElement.GetDouble(), 0.0f, 1.0f);
                var panelBits = 14;
                if (root.TryGetProperty("panelColorBits", out var bitsElement))
                    panelBits = bitsElement.GetInt32();

                if (layoutManager.HasCanvas(name))
                    return Results.Json(new ApiResponse<string>(false, Error: $"Canvas '{name}' already exists"));

                var canvas = layoutManager.CreateCustomCanvas(name, x, y, width, height, zOrder);
                canvas.Opacity = opacity;
                canvas.PanelColorBits = panelBits;

                // A brand-new canvas must not inherit a rotation config left over (in the global store) from a
                // previous layout that happened to use the same canvas name — that resurrected old content.
                ctx.RotationService?.Forget(name);

                return Results.Json(new ApiResponse<string>(true, $"Canvas '{name}' created"));
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResponse<string>(false, Error: ex.Message));
            }
        });

        // Get single canvas info
        app.MapGet("/api/canvas/{canvasName}", (string canvasName) =>
        {
            try
            {
                if (layoutManager == null)
                    return Results.Json(new ApiResponse<object>(false, Error: "Layout manager not initialized"));

                var canvas = ResolveCanvas(canvasName);
                if (canvas == null)
                    return Results.Json(new ApiResponse<object>(false, Error: $"Canvas '{canvasName}' not found"));

                return Results.Json(new ApiResponse<object>(true, new
                {
                    name = canvas.Name,
                    x = canvas.XPos,
                    y = canvas.YPos,
                    width = canvas.Width,
                    height = canvas.Height,
                    zOrder = canvas.ZOrder,
                    opacity = canvas.Opacity,
                    panelColorBits = canvas.PanelColorBits,
                    isVisible = !canvas.IsHidden,
                    transparentBackground = canvas.TransparentBackground
                }));
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResponse<object>(false, Error: ex.Message));
            }
        });

        // Swap z-order between canvases
        app.MapPost("/api/canvas/swap-zorder", async (HttpContext context) =>
        {
            try
            {
                if (layoutManager == null)
                    return Results.Json(new ApiResponse<string>(false, Error: "Layout manager not initialized"));

                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var jsonDoc = JsonDocument.Parse(body);
                var root = jsonDoc.RootElement;

                var canvas1Name = root.GetProperty("canvas1").GetString();
                var canvas2Name = root.GetProperty("canvas2").GetString();

                if (string.IsNullOrEmpty(canvas1Name) || string.IsNullOrEmpty(canvas2Name))
                    return Results.Json(new ApiResponse<string>(false, Error: "Both canvas names required"));

                var canvas1 = ResolveCanvas(canvas1Name);
                var canvas2 = ResolveCanvas(canvas2Name);

                if (canvas1 == null)
                    return Results.Json(new ApiResponse<string>(false, Error: $"Canvas '{canvas1Name}' not found"));
                if (canvas2 == null)
                    return Results.Json(new ApiResponse<string>(false, Error: $"Canvas '{canvas2Name}' not found"));

                var tempZOrder = canvas1.ZOrder;
                canvasManager.SetCanvasZOrder(canvas1, canvas2.ZOrder);
                canvasManager.SetCanvasZOrder(canvas2, tempZOrder);

                return Results.Json(new ApiResponse<string>(true, "Z-orders swapped"));
            }
            catch (Exception ex)
            {
                return Results.Json(new ApiResponse<string>(false, Error: ex.Message));
            }
        });
    }
}
