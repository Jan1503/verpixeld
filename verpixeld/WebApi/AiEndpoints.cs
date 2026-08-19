using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using verpixeld.Services;

namespace verpixeld.WebApi;

/// <summary>
///     API endpoints for AI image generation (Azure OpenAI / OpenAI) and chat configuration.
/// </summary>
public static class AiEndpoints
{
    public static void MapAiEndpoints(this WebApplication app)
    {
        var aiService = app.Services.GetRequiredService<AiImageService>();
        var chatService = app.Services.GetRequiredService<AiChatService>();

        var group = app.MapGroup("/api/ai");

        // Generate image from text prompt
        group.MapPost("/generate", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var json = JsonDocument.Parse(body).RootElement;

                var prompt = json.TryGetProperty("prompt", out var p) ? p.GetString() ?? "" : "";
                var style = json.TryGetProperty("style", out var s) ? s.GetString() ?? "" : "";
                var quality = json.TryGetProperty("quality", out var q) ? q.GetString() ?? "medium" : "medium";
                var canvasName = json.TryGetProperty("canvasName", out var c) ? c.GetString() ?? "Main" : "Main";
                var applyToDisplay = json.TryGetProperty("applyToDisplay", out var a) && a.GetBoolean();

                var result = await aiService.GenerateImageAsync(prompt, style, quality);

                if (result.Success && applyToDisplay && result.ImageBase64 != null)
                    aiService.ApplyToCanvas(result.ImageBase64, canvasName);

                return Results.Json(new
                {
                    success = result.Success,
                    error = result.Error,
                    imageBase64 = result.ImageBase64,
                    record = result.Record
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AI] Generate endpoint error: {ex.Message}");
                return Results.Json(new { success = false, error = ex.Message }, statusCode: 500);
            }
        });

        // Image-to-image edit/stylize
        group.MapPost("/edit", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var json = JsonDocument.Parse(body).RootElement;

                var imageBase64 = json.TryGetProperty("imageBase64", out var img) ? img.GetString() ?? "" : "";
                var prompt = json.TryGetProperty("prompt", out var p) ? p.GetString() ?? "" : "";
                var style = json.TryGetProperty("style", out var s) ? s.GetString() ?? "" : "";
                var canvasName = json.TryGetProperty("canvasName", out var c) ? c.GetString() ?? "Main" : "Main";
                var applyToDisplay = json.TryGetProperty("applyToDisplay", out var a) && a.GetBoolean();

                if (string.IsNullOrEmpty(imageBase64))
                    return Results.Json(new { success = false, error = "No image provided" });

                // Strip data URL prefix if present
                if (imageBase64.Contains(","))
                    imageBase64 = imageBase64.Split(',')[1];

                var result = await aiService.EditImageAsync(imageBase64, prompt, style);

                if (result.Success && applyToDisplay && result.ImageBase64 != null)
                    aiService.ApplyToCanvas(result.ImageBase64, canvasName);

                return Results.Json(new
                {
                    success = result.Success,
                    error = result.Error,
                    imageBase64 = result.ImageBase64,
                    record = result.Record
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AI] Edit endpoint error: {ex.Message}");
                return Results.Json(new { success = false, error = ex.Message }, statusCode: 500);
            }
        });

        // Apply an existing image (from history) to display
        group.MapPost("/apply", (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = reader.ReadToEndAsync().GetAwaiter().GetResult();
                var json = JsonDocument.Parse(body).RootElement;

                var imageBase64 = json.TryGetProperty("imageBase64", out var img) ? img.GetString() ?? "" : "";
                var canvasName = json.TryGetProperty("canvasName", out var c) ? c.GetString() ?? "Main" : "Main";

                if (string.IsNullOrEmpty(imageBase64))
                    return Results.Json(new { success = false, error = "No image provided" });

                if (imageBase64.Contains(","))
                    imageBase64 = imageBase64.Split(',')[1];

                var applied = aiService.ApplyToCanvas(imageBase64, canvasName);
                return Results.Json(new { success = applied });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message }, statusCode: 500);
            }
        });

        // Dismiss the image overlay (remove the overlay canvas so extension is visible again)
        group.MapPost("/dismiss", () =>
        {
            try
            {
                aiService.DismissImageOverlay();
                return Results.Json(new { success = true, message = "Image overlay dismissed" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message }, statusCode: 500);
            }
        });

        // Get status and configuration
        group.MapGet("/status", () =>
        {
            return Results.Json(new
            {
                success = true,
                configured = aiService.IsConfigured,
                generating = aiService.IsGenerating,
                provider = aiService.Provider,
                // Don't expose API keys
                azureEndpoint = aiService.AzureEndpoint,
                azureDeployment = aiService.AzureDeployment,
                azureChatDeployment = chatService.AzureChatDeployment,
                azureApiVersion = aiService.AzureApiVersion,
                chatConfigured = chatService.IsConfigured,
                azureKeySet = !string.IsNullOrEmpty(aiService.AzureApiKey),
                openAiKeySet = !string.IsNullOrEmpty(aiService.OpenAiApiKey),
                openAiModel = aiService.OpenAiModel,
                scheduleEnabled = aiService.ScheduleEnabled,
                scheduleIntervalMinutes = aiService.ScheduleIntervalMinutes,
                scheduleStyle = aiService.ScheduleStyle,
                scheduleCanvasName = aiService.ScheduleCanvasName,
                scheduleSaveToDisk = aiService.ScheduleSaveToDisk,
                schedulePrompts = aiService.SchedulePrompts
            });
        });

        // Configure AI provider settings
        group.MapPost("/configure", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var json = JsonDocument.Parse(body).RootElement;

                var provider = json.TryGetProperty("provider", out var prov) ? prov.GetString() ?? "azure" : "azure";
                var azureEndpoint = json.TryGetProperty("azureEndpoint", out var ae) ? ae.GetString() : null;
                var azureApiKey = json.TryGetProperty("azureApiKey", out var ak) ? ak.GetString() : null;
                var azureDeployment = json.TryGetProperty("azureDeployment", out var ad) ? ad.GetString() : null;
                var azureChatDeployment = json.TryGetProperty("azureChatDeployment", out var acd) ? acd.GetString() : null;
                var azureApiVersion = json.TryGetProperty("azureApiVersion", out var av) ? av.GetString() : null;
                var openAiApiKey = json.TryGetProperty("openAiApiKey", out var ok) ? ok.GetString() : null;
                var openAiModel = json.TryGetProperty("openAiModel", out var om) ? om.GetString() : null;

                // Configure image service
                aiService.Configure(provider, azureEndpoint, azureApiKey, azureDeployment, azureApiVersion, openAiApiKey, openAiModel);

                // Configure chat service (shares Azure credentials)
                chatService.Configure(azureEndpoint, azureApiKey, azureChatDeployment, azureApiVersion);

                return Results.Json(new
                {
                    success = true,
                    configured = aiService.IsConfigured,
                    provider = aiService.Provider,
                    message = "AI settings saved"
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message }, statusCode: 500);
            }
        });

        // Configure schedule
        group.MapPost("/schedule", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var json = JsonDocument.Parse(body).RootElement;

                var enabled = json.TryGetProperty("enabled", out var e) && e.GetBoolean();
                var interval = json.TryGetProperty("intervalMinutes", out var i) ? i.GetInt32() : 60;
                var style = json.TryGetProperty("style", out var s) ? s.GetString() ?? "pixel-art" : "pixel-art";
                var canvasName = json.TryGetProperty("canvasName", out var c) ? c.GetString() ?? "Main" : "Main";
                var saveToDisk = json.TryGetProperty("saveToDisk", out var sd) && sd.GetBoolean();
                var prompts = new List<string>();
                if (json.TryGetProperty("prompts", out var promptsArr) && promptsArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in promptsArr.EnumerateArray())
                    {
                        var line = item.GetString()?.Trim();
                        if (!string.IsNullOrEmpty(line))
                            prompts.Add(line);
                    }
                }

                aiService.ConfigureSchedule(enabled, interval, style, canvasName, saveToDisk, prompts);

                return Results.Json(new
                {
                    success = true,
                    scheduleEnabled = aiService.ScheduleEnabled,
                    message = enabled ? "Schedule enabled" : "Schedule disabled"
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message }, statusCode: 500);
            }
        });

        // Get generation history
        group.MapGet("/history", () =>
        {
            return Results.Json(new
            {
                success = true,
                history = aiService.History
            });
        });

        // Clear generation history
        group.MapDelete("/history", () =>
        {
            aiService.ClearHistory();
            return Results.Json(new { success = true, message = "History cleared" });
        });

        // ── Gallery endpoints ──────────────────────────────────────────

        // List saved gallery images (metadata only — no base64)
        group.MapGet("/gallery", () =>
        {
            return Results.Json(new
            {
                success = true,
                images = aiService.GetGalleryFiles(),
                count = aiService.GalleryCount
            });
        });

        // Get a single gallery image as base64
        group.MapGet("/gallery/{filename}", (string filename) =>
        {
            var base64 = aiService.GetGalleryImage(filename);
            if (base64 == null)
                return Results.Json(new { success = false, error = "Image not found" }, statusCode: 404);

            return Results.Json(new { success = true, imageBase64 = base64, filename });
        });

        // Serve gallery image as raw PNG (for <img src> usage)
        group.MapGet("/gallery/{filename}/thumb", (string filename) =>
        {
            var base64 = aiService.GetGalleryImage(filename);
            if (base64 == null)
                return Results.NotFound();
            var bytes = Convert.FromBase64String(base64);
            return Results.File(bytes, "image/png");
        });

        // Delete a gallery image
        group.MapDelete("/gallery/{filename}", (string filename) =>
        {
            var deleted = aiService.DeleteGalleryImage(filename);
            return Results.Json(new { success = deleted, message = deleted ? "Deleted" : "Not found" });
        });
    }
}
