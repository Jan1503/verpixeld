using System.Text.Json;
using CanvasManagement.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using verpixeld.Interfaces;
using verpixeld.Layout;
using verpixeld.Services;

namespace verpixeld.WebApi;

/// <summary>
///     Layout management API endpoints
/// </summary>
public static class LayoutEndpoints
{
    public static void MapLayoutEndpoints(this WebApplication app)
    {
        var ctx = app.Services.GetRequiredService<EndpointContext>();
        MapLayoutProfileEndpoints(app, ctx);
        MapCanvasEndpoints(app, ctx);
        MapSavedLayoutEndpoints(app, ctx);
    }

    private static void MapLayoutProfileEndpoints(WebApplication app, EndpointContext ctx)
    {
        // Get available layout profiles
        app.MapGet("/api/layout/profiles", () =>
        {
            try
            {
                var profiles = Enum.GetValues<LayoutProfile>()
                    .Select(p => new
                    {
                        name = p.ToString().ToLower(),
                        displayName = p.ToString(),
                        description = DisplayLayoutManager.GetLayoutDescription(p)
                    });

                return Results.Json(new { success = true, data = profiles });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        // Get current layout
        app.MapGet("/api/layout/current", () =>
        {
            try
            {
                var canvases = ctx.LayoutManager.GetAllCanvases()
                    .Select(c => new
                    {
                        name = c.Name,
                        width = c.Canvas.Width,
                        height = c.Canvas.Height
                    });

                return Results.Json(new
                {
                    success = true,
                    data = new
                    {
                        profile = ctx.LayoutManager.CurrentProfile.ToString().ToLower(),
                        displayName = ctx.LayoutManager.CurrentProfile.ToString(),
                        description = DisplayLayoutManager.GetLayoutDescription(ctx.LayoutManager.CurrentProfile),
                        canvasCount = ctx.LayoutManager.CanvasCount,
                        canvases
                    }
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        // Apply layout profile
        app.MapPost("/api/layout/apply/{profileName}", async (string profileName) =>
        {
            try
            {
                if (!Enum.TryParse<LayoutProfile>(profileName, true, out var profile))
                    return Results.Json(new
                    {
                        success = false,
                        error = $"Invalid layout profile '{profileName}'"
                    });

                Console.WriteLine($"[API] Applying layout: {profile}");

                // Clear active schedule - User is manually overriding
                ctx.ScheduleManager.ClearActiveSchedule();

                // Stop all current content
                Console.WriteLine("[API] Stopping all current content...");
                ctx.ContentManager.StopAllContent();

                // Give extensions time to fully stop
                await Task.Delay(200);
                Console.WriteLine("[API] All content stopped, applying new layout...");

                // A profile is a blank canvas structure, not a saved scene. Drop per-canvas rotation
                // playlists so Studio's Content pane (and later assigns) don't inherit the previous
                // layout's steps — same as loading a saved layout, which then re-imports from JSON.
                ctx.RotationService.ClearAll();

                // Apply new layout
                ctx.LayoutManager.ApplyLayout(profile);

                var prime = ctx.LayoutManager.GetCanvas("Main")
                            ?? ctx.LayoutManager.GetCanvas("Content")
                            ?? ctx.LayoutManager.GetAllCanvases().FirstOrDefault().Canvas;
                Console.WriteLine($"[API] Layout applied successfully. Prime canvas: {prime != null}");

                return Results.Json(new
                {
                    success = true,
                    data = new
                    {
                        profile = profile.ToString().ToLower(),
                        canvasCount = ctx.LayoutManager.CanvasCount,
                        canvases = ctx.LayoutManager.GetCanvasNames()
                    }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[API] Error applying layout: {ex.Message}");
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        // Get canvas list
        app.MapGet("/api/layout/canvases", () =>
        {
            try
            {
                var canvases = ctx.LayoutManager.GetAllCanvases()
                    .Select(c => new
                    {
                        name = c.Name,
                        width = c.Canvas.Width,
                        height = c.Canvas.Height
                    });

                return Results.Json(new { success = true, data = canvases });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message });
            }
        });
    }

    private static void MapCanvasEndpoints(WebApplication app, EndpointContext ctx)
    {
        // Assign extension to canvas
        app.MapPost("/api/layout/assign", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var jsonDoc = JsonDocument.Parse(body);

                if (!jsonDoc.RootElement.TryGetProperty("canvasName", out var canvasNameElement) ||
                    !jsonDoc.RootElement.TryGetProperty("extensionName", out var extensionNameElement))
                    return Results.Json(new { success = false, error = "Missing canvasName or extensionName" });

                var canvasName = canvasNameElement.GetString();
                var extensionName = extensionNameElement.GetString();

                if (string.IsNullOrEmpty(canvasName) || string.IsNullOrEmpty(extensionName))
                    return Results.Json(new { success = false, error = "Invalid canvasName or extensionName" });

                if (SystemOverlayCanvases.IsSystem(canvasName))
                    return Results.Json(new { success = false, error = $"'{canvasName}' is a host overlay and cannot take content" });

                Console.WriteLine($"[API] Assigning '{extensionName}' to canvas '{canvasName}'");

                // Extract and convert config if present
                Dictionary<string, object>? config = null;
                if (jsonDoc.RootElement.TryGetProperty("config", out var configElement) &&
                    configElement.ValueKind == JsonValueKind.Object)
                {
                    config = new Dictionary<string, object>();
                    foreach (var property in configElement.EnumerateObject())
                    {
                        var value = property.Value.ValueKind switch
                        {
                            JsonValueKind.Number => property.Value.TryGetInt32(out var intVal)
                                ? (object)intVal
                                : property.Value.TryGetInt64(out var longVal)
                                    ? longVal
                                    : property.Value.GetDouble(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                            _ => property.Value.ToString()
                        };

                        // Convert camelCase to PascalCase for property names
                        var propName = char.ToUpper(property.Name[0]) + property.Name.Substring(1);
                        config[propName] = value;
                    }
                }

                // Use content manager to assign extension
                var content = ctx.ContentManager!.AssignExtension(canvasName, extensionName, config);

                return Results.Json(new
                {
                    success = true,
                    data = new
                    {
                        canvasName = content.CanvasName,
                        extensionName = content.ExtensionDisplayName,
                        contentType = content.ContentType.ToString(),
                        startedAt = content.StartedAt
                    }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[API] Error assigning extension: {ex.Message}");
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        // Stop content on canvas
        app.MapPost("/api/layout/stop/{canvasName}", (string canvasName) =>
        {
            try
            {
                Console.WriteLine($"[API] Stopping content on canvas '{canvasName}'");
                ctx.ContentManager!.StopContent(canvasName);
                return Results.Json(new { success = true, message = $"Content stopped on '{canvasName}'" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        // Get all content (returns summary format expected by frontend)
        app.MapGet("/api/layout/content", () =>
        {
            try
            {
                var contents = ctx.ContentManager!.GetAllContents().ToList();
                var totalCanvases = ctx.LayoutManager.CanvasCount;

                // Map to serializable DTOs (ExtensionInstance contains non-serializable Type)
                // Return in the format expected by the frontend: { contents: [...] }
                var contentList = contents.Select(c => new
                {
                    canvasName = c.CanvasName,
                    contentType = c.ContentType.ToString(),
                    extensionName = c.ExtensionDisplayName,
                    uptime = c.Uptime,
                    startedAt = c.StartedAt,
                    configuration = c.Configuration
                }).ToList();

                var summary = new
                {
                    totalCanvases,
                    canvasesWithContent = contents.Count,
                    canvasesEmpty = totalCanvases - contents.Count,
                    contents = contentList
                };

                return Results.Json(new { success = true, data = summary });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        // Get content for specific canvas
        app.MapGet("/api/layout/content/{canvasName}", (string canvasName) =>
        {
            try
            {
                var content = ctx.ContentManager!.GetContent(canvasName);
                if (content == null)
                    return Results.Json(new { success = false, error = $"No content on canvas '{canvasName}'" });

                // Get current parameters from extension instance
                JsonElement? currentParams = null;
                if (content.ExtensionInstance != null)
                {
                    var raw = new Dictionary<string, object>();
                    var instance = ResolveExtensionInstance(content.ExtensionInstance);
                    var props = instance.GetType().GetProperties()
                        .Where(p => p.CanRead && p.CanWrite);

                    foreach (var prop in props)
                        try
                        {
                            var value = prop.GetValue(instance);
                            if (value != null) raw[prop.Name] = value;
                        }
                        catch
                        {
                            // Ignore properties that throw
                        }

                    // Normalise via the shared options so colours become #AARRGGBB strings, enums become
                    // names and nested lists/objects become plain JSON the GUI editor can render directly.
                    currentParams = JsonSerializer.SerializeToElement(raw, ExtensionJson.Options);
                }

                return Results.Json(new
                {
                    success = true,
                    data = new
                    {
                        canvasName = content.CanvasName,
                        extensionName = content.ExtensionDisplayName,
                        extensionTypeName = content.ExtensionInstance?.GetType().Name,
                        contentType = content.ContentType.ToString(),
                        startedAt = content.StartedAt,
                        currentParameters = currentParams
                    }
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        // Configure extension parameters
        app.MapPost("/api/layout/configure/{canvasName}", async (string canvasName, HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var jsonDoc = JsonDocument.Parse(body);

                var config = new Dictionary<string, object>();
                foreach (var property in jsonDoc.RootElement.EnumerateObject())
                {
                    var value = property.Value.ValueKind switch
                    {
                        JsonValueKind.Number => property.Value.TryGetInt32(out var intVal)
                            ? (object)intVal
                            : property.Value.TryGetInt64(out var longVal)
                                ? longVal
                                : property.Value.GetDouble(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                        _ => property.Value.ToString()
                    };

                    // Convert camelCase to PascalCase
                    var propName = char.ToUpper(property.Name[0]) + property.Name.Substring(1);
                    config[propName] = value;
                }

                ctx.ContentManager!.UpdateConfiguration(canvasName, config);

                return Results.Json(new { success = true, message = "Configuration updated" });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[API] Error configuring canvas: {ex.Message}");
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        // Restart extension on canvas
        app.MapPost("/api/layout/restart/{canvasName}", (string canvasName) =>
        {
            try
            {
                Console.WriteLine($"[API] Restarting extension on canvas '{canvasName}'");
                ctx.ContentManager!.RestartContent(canvasName);
                return Results.Json(new { success = true, message = $"Extension restarted on '{canvasName}'" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        // Invoke method on extension
        app.MapPost("/api/layout/invoke/{canvasName}", async (string canvasName, HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var jsonDoc = JsonDocument.Parse(body);

                if (!jsonDoc.RootElement.TryGetProperty("methodName", out var methodNameElement))
                    return Results.Json(new { success = false, error = "Missing methodName" });

                var methodName = methodNameElement.GetString();
                if (string.IsNullOrEmpty(methodName))
                    return Results.Json(new { success = false, error = "Invalid methodName" });

                // Extract parameters if present
                object[]? parameters = null;
                JsonElement paramsElement = default;
                var hasParams = jsonDoc.RootElement.TryGetProperty("parameters", out paramsElement)
                                || jsonDoc.RootElement.TryGetProperty("args", out paramsElement);
                if (hasParams && paramsElement.ValueKind == JsonValueKind.Array)
                    parameters = paramsElement.EnumerateArray()
                        .Select(p => p.ValueKind switch
                        {
                            JsonValueKind.Number => p.TryGetInt32(out var intVal)
                                ? (object)intVal
                                : p.TryGetInt64(out var longVal)
                                    ? longVal
                                    : p.GetDouble(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            JsonValueKind.String => p.GetString() ?? string.Empty,
                            _ => p.ToString()
                        })
                        .ToArray();

                var result = ctx.ContentManager!.InvokeMethod(canvasName, methodName, parameters);

                return Results.Json(new
                {
                    success = true,
                    data = new
                    {
                        methodName,
                        result = result?.ToString()
                    }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[API] Error invoking method: {ex.Message}");
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        // Get available methods for canvas extension
        app.MapGet("/api/layout/methods/{canvasName}", (string canvasName) =>
        {
            try
            {
                var methods = ctx.ContentManager!.GetAvailableMethods(canvasName);
                return Results.Json(new { success = true, data = methods });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message });
            }
        });
    }

    private static void MapSavedLayoutEndpoints(WebApplication app, EndpointContext ctx)
    {
        // Get all saved layouts
        app.MapGet("/api/layout/saved", () =>
        {
            try
            {
                var layouts = ctx.LayoutStorageManager!.GetAllLayouts();
                return Results.Json(new { success = true, data = layouts });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        // Save current layout
        app.MapPost("/api/layout/save", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var jsonDoc = JsonDocument.Parse(body);

                if (!jsonDoc.RootElement.TryGetProperty("name", out var nameElement))
                    return Results.Json(new { success = false, error = "Layout name is required" });

                var name = nameElement.GetString();
                if (string.IsNullOrEmpty(name))
                    return Results.Json(new { success = false, error = "Layout name cannot be empty" });

                var description = "";
                if (jsonDoc.RootElement.TryGetProperty("description", out var descElement))
                    description = descElement.GetString() ?? "";

                // Whether this layout should auto-load on startup. Was previously dropped here, so the default
                // flag never persisted and nothing was restored after a restart.
                var isDefault = jsonDoc.RootElement.TryGetProperty("isDefault", out var defElement) &&
                                defElement.ValueKind == JsonValueKind.True;

                // Whether this layout should re-apply its (current) brightness on load. Defaults to true, but
                // was previously never read — so unchecking "Apply this layout's brightness" had no effect.
                var overrideBrightness = true;
                if (jsonDoc.RootElement.TryGetProperty("overrideGlobalBrightness", out var ovEl))
                    overrideBrightness = ovEl.ValueKind != JsonValueKind.False;

                Console.WriteLine(
                    $"[API] Saving layout '{name}'... (default: {isDefault}, applyBrightness: {overrideBrightness})");

                // Build saved layout from current state
                var savedLayout = new SavedLayout
                {
                    Name = name,
                    Description = description,
                    Profile = ctx.LayoutManager.CurrentProfile.ToString(),
                    CreatedAt = DateTime.UtcNow,
                    IsDefault = isDefault,
                    OverrideGlobalBrightness = overrideBrightness,
                    GlobalBrightness = ctx.CanvasManager.Brightness,
                    Canvases = new Dictionary<string, CanvasConfiguration>()
                };

                // Get current content from content manager
                var contents = ctx.ContentManager!.GetAllContents();
                foreach (var content in contents)
                {
                    if (content.ExtensionInstance == null) continue;

                    var canvasConfig = new CanvasConfiguration
                    {
                        ExtensionName = content.ExtensionDisplayName,
                        Configuration = new Dictionary<string, object>()
                    };

                    // Capture the live canvas geometry/stacking so multi-canvas layouts (e.g. a second
                    // full-screen layer or an overlay) can be recreated on load even when the profile only
                    // defines a single base canvas.
                    var liveCanvas = ctx.CanvasManager.GetCanvasByName(content.CanvasName);
                    if (liveCanvas != null)
                    {
                        canvasConfig.ZOrder = liveCanvas.ZOrder;
                        canvasConfig.Opacity = liveCanvas.Opacity;
                        canvasConfig.PanelColorBits = liveCanvas.PanelColorBits;
                        canvasConfig.Brightness = liveCanvas.Brightness;
                        canvasConfig.X = liveCanvas.XPos;
                        canvasConfig.Y = liveCanvas.YPos;
                        canvasConfig.Width = liveCanvas.Width;
                        canvasConfig.Height = liveCanvas.Height;
                        canvasConfig.IsOverlay = !(liveCanvas.XPos == 0 && liveCanvas.YPos == 0 &&
                                                   liveCanvas.Width == ctx.CanvasManager.Width &&
                                                   liveCanvas.Height == ctx.CanvasManager.Height);
                        canvasConfig.TransparentBackground = liveCanvas.TransparentBackground;
                        canvasConfig.Hidden = liveCanvas.IsHidden;
                    }

                    // Extract current parameters from the REAL extension instance (the [ExtensionParameter]
                    // properties live on the inner instance, not the dynamic wrapper). Only persist properties
                    // explicitly marked as parameters — never runtime/interface state such as IsRunning, which
                    // would re-arm to true on load and make Start() early-return (leaving a blank canvas).
                    var instance = ResolveExtensionInstance(content.ExtensionInstance);
                    var props = instance.GetType().GetProperties()
                        .Where(p => p.CanRead && p.CanWrite &&
                                    p.GetCustomAttributes(typeof(ExtensionParameterAttribute), false).Any());

                    foreach (var prop in props)
                        try
                        {
                            var value = prop.GetValue(instance);
                            if (value == null) continue;

                            if (IsSerializableValue(value))
                            {
                                canvasConfig.Configuration[prop.Name] = value;
                            }
                            else
                            {
                                // Structured / colour parameters: persist as a JSON element (hex colours,
                                // enum names, nested lists) so they round-trip at a single encoding level.
                                // Using Serialize (string) here would double-encode, e.g. colours come back as
                                // "\"#AARRGGBB\"" and fail to parse on load.
                                canvasConfig.Configuration[prop.Name] =
                                    JsonSerializer.SerializeToElement(value, ExtensionJson.Options);
                            }
                        }
                        catch
                        {
                            // Ignore properties that throw
                        }

                    // Persist this canvas's content rotation (if any) with the layout so it restores on load.
                    var rotation = ctx.RotationService.GetConfig(content.CanvasName);
                    if (rotation is { Steps.Count: > 0 }) canvasConfig.Rotation = rotation;

                    savedLayout.Canvases[content.CanvasName] = canvasConfig;
                }

                ctx.LayoutStorageManager!.SaveLayout(savedLayout);

                Console.WriteLine(
                    $"[API] Layout '{name}' saved with {savedLayout.Canvases.Count} canvas configurations");

                return Results.Json(new
                {
                    success = true,
                    data = new
                    {
                        name = savedLayout.Name,
                        description = savedLayout.Description,
                        profile = savedLayout.Profile,
                        contentCount = savedLayout.Canvases.Count,
                        createdAt = savedLayout.CreatedAt
                    }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[API] Error saving layout: {ex.Message}");
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        // Load saved layout
        app.MapPost("/api/layout/load/{layoutName}", async (string layoutName) =>
        {
            try
            {
                Console.WriteLine($"[API] Loading layout '{layoutName}'...");

                var layout = ctx.LayoutStorageManager!.LoadLayout(layoutName);
                if (layout == null)
                    return Results.Json(new { success = false, error = $"Layout '{layoutName}' not found" });

                // Clear active schedule - User is manually overriding
                ctx.ScheduleManager.ClearActiveSchedule();

                // Use the centralized layout loader service
                var result = await ctx.LayoutLoader.LoadLayoutAsync(layout, "API");

                return Results.Json(new
                {
                    success = result.Success,
                    data = result.Success
                        ? new
                        {
                            name = layout.Name,
                            profile = layout.Profile,
                            loadedCount = result.CanvasesRestored,
                            totalCount = layout.Canvases.Count,
                            filtersRestored = result.FiltersRestored,
                            message = $"Layout '{layout.Name}' loaded successfully"
                        }
                        : null,
                    error = result.ErrorMessage
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[API] Error loading layout: {ex.Message}");
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        // Delete saved layout
        app.MapDelete("/api/layout/saved/{layoutName}", (string layoutName) =>
        {
            try
            {
                var deleted = ctx.LayoutStorageManager!.DeleteLayout(layoutName);
                if (!deleted)
                    return Results.Json(new { success = false, error = $"Layout '{layoutName}' not found" });

                Console.WriteLine($"[API] Layout '{layoutName}' deleted");
                return Results.Json(new { success = true, message = $"Layout '{layoutName}' deleted" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        // Get specific saved layout
        app.MapGet("/api/layout/saved/{layoutName}", (string layoutName) =>
        {
            try
            {
                var layout = ctx.LayoutStorageManager!.LoadLayout(layoutName);
                if (layout == null)
                    return Results.Json(new { success = false, error = $"Layout '{layoutName}' not found" });

                return Results.Json(new { success = true, data = layout });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        // Set layout as default
        app.MapPost("/api/layout/saved/{layoutName}/set-default", (string layoutName) =>
        {
            try
            {
                var layout = ctx.LayoutStorageManager!.LoadLayout(layoutName);
                if (layout == null)
                    return Results.Json(new { success = false, error = $"Layout '{layoutName}' not found" });

                ctx.LayoutStorageManager.SetDefaultLayout(layoutName);
                Console.WriteLine($"[API] Layout '{layoutName}' set as default");

                return Results.Json(new { success = true, message = $"Layout '{layoutName}' set as default" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        // Clear default layout
        app.MapPost("/api/layout/saved/{layoutName}/clear-default", (string layoutName) =>
        {
            try
            {
                var defaultLayout = ctx.LayoutStorageManager!.GetDefaultLayout();
                if (defaultLayout == null || defaultLayout.Name != layoutName)
                    return Results.Json(new { success = false, error = $"Layout '{layoutName}' is not the default" });

                ctx.LayoutStorageManager.ClearDefaultLayout(layoutName);
                Console.WriteLine("[API] Default layout cleared");

                return Results.Json(new { success = true, message = "Default layout cleared" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message });
            }
        });
    }

    /// <summary>
    ///     Unwraps a dynamic-extension wrapper to its inner instance, where the [ExtensionParameter]
    ///     properties actually live (the wrapper itself has none, which is why saved layouts came out empty).
    /// </summary>
    private static object ResolveExtensionInstance(object ext)
    {
        var inner = ext.GetType().GetProperty("Instance")?.GetValue(ext);
        return inner ?? ext;
    }

    private static bool IsSerializableValue(object value)
    {
        var type = value.GetType();
        return type.IsPrimitive ||
               type == typeof(string) ||
               type == typeof(decimal) ||
               type == typeof(DateTime) ||
               type.IsEnum;
    }
}
