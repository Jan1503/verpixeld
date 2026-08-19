using System.Text.Json;
using verpixeld.Configuration;
using verpixeld.Layout;
using verpixeld.Services;

namespace verpixeld.WebApi;

/// <summary>REST API for per-canvas content rotation.</summary>
public static class CanvasRotationEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static void MapCanvasRotationEndpoints(this WebApplication app, CanvasRotationService svc)
    {
        app.MapGet("/api/canvas/{name}/rotation", (string name) =>
        {
            var cfg = svc.GetConfig(name);
            return Results.Json(new ApiResponse<object>(true, new
            {
                isRunning = svc.IsRunning(name),
                enabled = cfg.Enabled,
                intervalSeconds = cfg.IntervalSeconds,
                loop = cfg.Loop,
                transition = cfg.Transition.ToString(),
                activeIndex = svc.GetActiveIndex(name),
                steps = cfg.Steps.Select(s => new { type = s.Type, extension = s.Extension, detail = StepDetail(s) }).ToList()
            }));
        });

        // Add a content item with a chosen extension (and optional config) — used by the Content list UI.
        app.MapPost("/api/canvas/{name}/rotation/add-extension", async (string name, HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            var req = JsonSerializer.Deserialize<AddExtensionRequest>(body, JsonOpts);
            if (req == null || string.IsNullOrWhiteSpace(req.Extension))
                return Results.Json(new ApiResponse<string>(false, Error: "Extension is required"));
            svc.AddStep(name, req.Extension, req.Config);
            return Results.Json(new ApiResponse<string>(true, "Item added"));
        });

        app.MapGet("/api/canvas/{name}/rotation/step/{index:int}", (string name, int index) =>
        {
            var step = svc.GetStep(name, index);
            return step == null
                ? Results.Json(new ApiResponse<object>(false, Error: "Step not found"))
                : Results.Json(new ApiResponse<object>(true, new { extension = step.Extension, config = step.Config }));
        });

        app.MapPut("/api/canvas/{name}/rotation/step/{index:int}/config", async (string name, int index, HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            var config = JsonSerializer.Deserialize<Dictionary<string, object>>(body, JsonOpts);
            svc.SetStepConfig(name, index, config);
            return Results.Json(new ApiResponse<string>(true, "Item updated"));
        });

        app.MapPost("/api/canvas/{name}/rotation/duplicate-step", (string name, int index) =>
        {
            svc.DuplicateStep(name, index);
            return Results.Json(new ApiResponse<string>(true, "Item duplicated"));
        });

        app.MapPost("/api/canvas/{name}/rotation/add-media", async (string name, HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            var req = JsonSerializer.Deserialize<AddMediaRequest>(body, JsonOpts);
            if (req == null || string.IsNullOrWhiteSpace(req.File))
                return Results.Json(new ApiResponse<string>(false, Error: "File is required"));
            svc.AddMedia(name, req.File, req.Loop);
            return Results.Json(new ApiResponse<string>(true, "Media added"));
        });

        app.MapPost("/api/canvas/{name}/rotation/add-camera", async (string name, HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            var req = string.IsNullOrWhiteSpace(body)
                ? new AddCameraRequest()
                : JsonSerializer.Deserialize<AddCameraRequest>(body, JsonOpts) ?? new AddCameraRequest();
            svc.AddCamera(name, req.Device, req.Effect);
            return Results.Json(new ApiResponse<string>(true, "Camera added"));
        });

        app.MapPost("/api/canvas/{name}/rotation/apply-step", async (string name, int index) =>
        {
            var ok = await svc.ApplyStep(name, index);
            return Results.Json(ok
                ? new ApiResponse<string>(true, "Step applied")
                : new ApiResponse<string>(false, Error: "Step not found"));
        });

        app.MapPost("/api/canvas/{name}/rotation/update-step", (string name, int index) =>
        {
            var ok = svc.UpdateStep(name, index);
            return Results.Json(ok
                ? new ApiResponse<string>(true, "Step updated")
                : new ApiResponse<string>(false, Error: "No content on canvas to capture"));
        });

        app.MapPost("/api/canvas/{name}/rotation/add-current", (string name) =>
        {
            var ok = svc.AddCurrentAsStep(name);
            return Results.Json(ok
                ? new ApiResponse<string>(true, "Step added")
                : new ApiResponse<string>(false, Error: "No content assigned to this canvas to capture"));
        });

        app.MapPost("/api/canvas/{name}/rotation/remove-step", (string name, int index) =>
        {
            svc.RemoveStep(name, index);
            return Results.Json(new ApiResponse<string>(true, "Step removed"));
        });

        app.MapPost("/api/canvas/{name}/rotation/move-step", (string name, int index, int dir) =>
        {
            svc.MoveStep(name, index, dir);
            return Results.Json(new ApiResponse<string>(true, "Step moved"));
        });

        app.MapPost("/api/canvas/{name}/rotation/settings", async (string name, HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            var req = JsonSerializer.Deserialize<RotationSettingsRequest>(body, JsonOpts)
                      ?? new RotationSettingsRequest();
            svc.UpdateSettings(name, req.IntervalSeconds, req.Transition, req.Loop, req.Enabled);
            return Results.Json(new ApiResponse<object>(true,
                new { isRunning = svc.IsRunning(name), config = svc.GetConfig(name) }));
        });

        app.MapPost("/api/canvas/{name}/rotation/start", (string name) =>
        {
            svc.Start(name);
            return Results.Json(new ApiResponse<string>(true, "Rotation started"));
        });

        app.MapPost("/api/canvas/{name}/rotation/stop", (string name) =>
        {
            svc.Stop(name);
            return Results.Json(new ApiResponse<string>(true, "Rotation stopped"));
        });

        app.MapPost("/api/canvas/{name}/rotation/next", (string name) =>
        {
            svc.Next(name);
            return Results.Json(new ApiResponse<string>(true, "Advanced"));
        });

        // Global suspend/resume for all per-canvas rotations (used while the Layout Editor is open).
        app.MapPost("/api/rotations/suspend-all", () =>
        {
            var wasRunning = svc.SuspendAll();
            return Results.Json(new ApiResponse<object>(true, new { wasRunning }));
        });

        app.MapPost("/api/rotations/resume-all", () =>
        {
            svc.ResumeAll();
            return Results.Json(new ApiResponse<string>(true, "Resumed"));
        });
    }

    // Best-effort human label for a step so duplicates of the same extension can be told apart.
    private static readonly string[] LabelKeys =
        { "Label", "EntityId", "Location", "City", "Text", "Message", "Title", "Name", "Url", "Symbol" };

    private static string StepDetail(RotationStep s)
    {
        if (string.Equals(s.Type, "camera", StringComparison.OrdinalIgnoreCase))
        {
            var fx = s.Config != null && s.Config.TryGetValue("effect", out var e)
                ? ConfigNormalizer.NormalizeValue(e)?.ToString()
                : null;
            return string.IsNullOrWhiteSpace(fx) || fx == "none" ? "USB camera" : "USB camera · " + fx;
        }

        if (string.Equals(s.Type, "media", StringComparison.OrdinalIgnoreCase))
        {
            if (s.Config != null && s.Config.TryGetValue("file", out var f))
            {
                var name = ConfigNormalizer.NormalizeValue(f)?.ToString();
                if (!string.IsNullOrWhiteSpace(name)) return Trim(name);
            }
            return "video";
        }

        if (s.Config == null || s.Config.Count == 0) return "";

        foreach (var key in LabelKeys)
            if (s.Config.TryGetValue(key, out var v))
            {
                var str = ConfigNormalizer.NormalizeValue(v)?.ToString();
                if (!string.IsNullOrWhiteSpace(str)) return Trim(str);
            }

        // Fallback: first non-empty config value.
        foreach (var kv in s.Config)
        {
            var str = ConfigNormalizer.NormalizeValue(kv.Value)?.ToString();
            if (!string.IsNullOrWhiteSpace(str)) return $"{kv.Key}={Trim(str)}";
        }

        return "";
    }

    private static string Trim(string s) => s.Length > 30 ? "…" + s[^29..] : s;

    private class AddExtensionRequest
    {
        public string Extension { get; set; } = string.Empty;
        public Dictionary<string, object>? Config { get; set; }
    }

    private class AddMediaRequest
    {
        public string File { get; set; } = string.Empty;
        public bool Loop { get; set; } = true;
    }

    private class AddCameraRequest
    {
        public string? Device { get; set; }
        public string? Effect { get; set; }
    }

    private class RotationSettingsRequest
    {
        public bool Enabled { get; set; }
        public int IntervalSeconds { get; set; } = 12;
        public bool Loop { get; set; } = true;
        public CanvasTransition Transition { get; set; } = CanvasTransition.Fade;
    }
}
