using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using verpixeld.Configuration;
using verpixeld.Services;

namespace verpixeld.WebApi;

/// <summary>
///     API endpoints for voice commands (Azure Speech) and local USB camera.
/// </summary>
public static class VoiceEndpoints
{
    public static void MapVoiceEndpoints(this WebApplication app)
    {
        var voiceService = app.Services.GetRequiredService<VoiceCommandService>();
        var cameraService = app.Services.GetRequiredService<LocalCameraService>();

        // ── Voice Command Endpoints ────────────────────────────────

        var voice = app.MapGroup("/api/voice");

        // Get voice command status
        voice.MapGet("/status", () =>
        {
            return Results.Json(new
            {
                success = true,
                enabled = voiceService.Enabled,
                configured = voiceService.IsConfigured,
                sdkAvailable = voiceService.SdkAvailable,
                hasKeywordModel = voiceService.HasKeywordModel,
                state = voiceService.CurrentState.ToString().ToLowerInvariant(),
                lastTranscription = voiceService.LastTranscription,
                lastError = voiceService.LastError,
                lastCommandTime = voiceService.LastCommandTime?.ToString("o"),
                commandCount = voiceService.CommandCount,
                // Config
                speechRegion = voiceService.SpeechRegion,
                keywordModelPath = voiceService.KeywordModelPath,
                audioDevice = voiceService.AudioDevice,
                videoDevice = voiceService.VideoDevice,
                defaultStyle = voiceService.DefaultStyle,
                speechLanguage = voiceService.SpeechLanguage,
                displayDurationSeconds = voiceService.DisplayDurationSeconds,
                silenceTimeoutMs = voiceService.SilenceTimeoutMs,
                profanityFilter = voiceService.ProfanityFilter,
                segmentationStrategy = voiceService.SegmentationStrategy,
                ttsEnabled = voiceService.TtsEnabled,
                ttsVoiceName = voiceService.TtsVoiceName,
                musicAudioOnly = voiceService.MusicAudioOnly,
                saveGeneratedImages = voiceService.SaveGeneratedImages,
                ttsDuckingEnabled = voiceService.TtsDuckingEnabled,
                ttsDuckVolumePercent = voiceService.TtsDuckVolumePercent,
                lastIntent = voiceService.LastIntent,
                lastResponse = voiceService.LastResponse
            });
        });

        // Configure voice settings
        voice.MapPost("/configure", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var json = JsonDocument.Parse(body).RootElement;

                voiceService.Configure(
                    speechKey: json.TryGetProperty("speechKey", out var sk) ? sk.GetString() : null,
                    speechRegion: json.TryGetProperty("speechRegion", out var sr) ? sr.GetString() : null,
                    keywordModelPath: json.TryGetProperty("keywordModelPath", out var kp) ? kp.GetString() : null,
                    audioDevice: json.TryGetProperty("audioDevice", out var ad) ? ad.GetString() : null,
                    videoDevice: json.TryGetProperty("videoDevice", out var vd) ? vd.GetString() : null,
                    defaultStyle: json.TryGetProperty("defaultStyle", out var ds) ? ds.GetString() : null,
                    speechLanguage: json.TryGetProperty("speechLanguage", out var sl) ? sl.GetString() : null,
                    displayDuration: json.TryGetProperty("displayDurationSeconds", out var dd) ? dd.GetInt32() : null,
                    enabled: json.TryGetProperty("enabled", out var en) ? en.GetBoolean() : null,
                    silenceTimeoutMs: json.TryGetProperty("silenceTimeoutMs", out var st) ? st.GetInt32() : null,
                    profanityFilter: json.TryGetProperty("profanityFilter", out var pf) ? pf.GetString() : null,
                    segmentationStrategy: json.TryGetProperty("segmentationStrategy", out var ss) ? ss.GetString() : null,
                    ttsEnabled: json.TryGetProperty("ttsEnabled", out var te) ? te.GetBoolean() : null,
                    ttsVoiceName: json.TryGetProperty("ttsVoiceName", out var tv) ? tv.GetString() : null,
                    musicAudioOnly: json.TryGetProperty("musicAudioOnly", out var ma) ? ma.GetBoolean() : null,
                    saveGeneratedImages: json.TryGetProperty("saveGeneratedImages", out var sg) ? sg.GetBoolean() : null,
                    ttsDuckingEnabled: json.TryGetProperty("ttsDuckingEnabled", out var tde) ? tde.GetBoolean() : null,
                    ttsDuckVolumePercent: json.TryGetProperty("ttsDuckVolumePercent", out var tdv) ? tdv.GetInt32() : null
                );

                return Results.Json(new
                {
                    success = true,
                    enabled = voiceService.Enabled,
                    state = voiceService.CurrentState.ToString().ToLowerInvariant()
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message }, statusCode: 500);
            }
        });

        // Start/stop voice listening
        voice.MapPost("/start", () =>
        {
            voiceService.Start();
            return Results.Json(new
            {
                success = true,
                state = voiceService.CurrentState.ToString().ToLowerInvariant()
            });
        });

        voice.MapPost("/stop", () =>
        {
            voiceService.Stop();
            return Results.Json(new { success = true, state = "disabled" });
        });

        // Manual trigger (push-to-talk via API)
        voice.MapPost("/trigger", async () =>
        {
            try
            {
                var transcription = await voiceService.ManualTriggerAsync();
                return Results.Json(new
                {
                    success = transcription != null,
                    transcription,
                    error = transcription == null ? voiceService.LastError : null,
                    state = voiceService.CurrentState.ToString().ToLowerInvariant()
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message }, statusCode: 500);
            }
        });

        // Upload keyword model file (.table)
        voice.MapPost("/keyword-upload", async (HttpContext context) =>
        {
            try
            {
                if (!context.Request.HasFormContentType)
                    return Results.Json(new { success = false, error = "Expected multipart form data" }, statusCode: 400);

                var form = await context.Request.ReadFormAsync();
                var file = form.Files.GetFile("keywordFile");
                if (file == null || file.Length == 0)
                    return Results.Json(new { success = false, error = "No file uploaded" }, statusCode: 400);

                var filePath = AppPaths.KeywordModel;

                await using var fs = new FileStream(filePath, FileMode.Create);
                await file.CopyToAsync(fs);

                voiceService.Configure(
                    speechKey: null, speechRegion: null,
                    keywordModelPath: filePath,
                    audioDevice: null, videoDevice: null,
                    defaultStyle: null, speechLanguage: null,
                    displayDuration: null, enabled: null
                );

                Console.WriteLine($"[VOICE] Keyword model uploaded: {filePath} ({file.Length} bytes)");

                return Results.Json(new
                {
                    success = true,
                    path = filePath,
                    sizeBytes = file.Length,
                    hasKeywordModel = voiceService.HasKeywordModel
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message }, statusCode: 500);
            }
        }).DisableAntiforgery();

        // ── Local Camera Endpoints ─────────────────────────────────

        var cam = app.MapGroup("/api/localcam");

        // Get camera status
        cam.MapGet("/status", () =>
        {
            return Results.Json(new
            {
                success = true,
                streaming = cameraService.IsStreaming,
                videoDevice = cameraService.VideoDevice,
                fps = cameraService.Fps,
                scaleFilter = cameraService.ScaleFilter,
                inputFormat = cameraService.InputFormat,
                inputResolution = cameraService.InputResolution,
                activeEffect = cameraService.ActiveEffect
            });
        });

        // Configure camera
        cam.MapPost("/configure", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var json = JsonDocument.Parse(body).RootElement;

                cameraService.Configure(
                    videoDevice: json.TryGetProperty("videoDevice", out var vd) ? vd.GetString() : null,
                    fps: json.TryGetProperty("fps", out var f) ? f.GetInt32() : null,
                    scaleFilter: json.TryGetProperty("scaleFilter", out var sf) ? sf.GetString() : null,
                    inputFormat: json.TryGetProperty("inputFormat", out var inf) ? inf.GetString() : null,
                    inputResolution: json.TryGetProperty("inputResolution", out var ir) ? ir.GetString() : null,
                    activeEffect: json.TryGetProperty("activeEffect", out var ae) ? ae.GetString() : null
                );

                return Results.Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message }, statusCode: 500);
            }
        });

        // Start/stop camera stream
        cam.MapPost("/start", () =>
        {
            var started = cameraService.StartStream();
            return Results.Json(new { success = started });
        });

        cam.MapPost("/stop", () =>
        {
            cameraService.StopStream();
            return Results.Json(new { success = true });
        });

        // Capture single frame (returns base64)
        cam.MapGet("/capture", async () =>
        {
            try
            {
                var base64 = await cameraService.CaptureFrameAsync();
                if (base64 == null)
                    return Results.Json(new { success = false, error = "Failed to capture frame" });

                return Results.Json(new { success = true, imageBase64 = base64 });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message }, statusCode: 500);
            }
        });

        // List available devices
        cam.MapGet("/devices", () =>
        {
            var videoDevices = LocalCameraService.ListVideoDevices();
            var audioDevices = LocalCameraService.ListAudioDevices();

            Console.WriteLine($"[DEVICES] Found {videoDevices.Count} video, {audioDevices.Count} audio devices");
            foreach (var d in audioDevices)
                Console.WriteLine($"[DEVICES] Audio: {d.Name} → {d.Path}");

            return Results.Json(new
            {
                success = true,
                videoDevices = videoDevices.Select(d => new { path = d.Path, name = d.Name }),
                audioDevices = audioDevices.Select(d => new { path = d.Path, name = d.Name })
            });
        });
    }
}
