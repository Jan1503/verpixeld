using System.Text.Json;
using System.Text.Json.Nodes;
using verpixeld.Hardware;
using verpixeld.Services;

namespace verpixeld.WebApi;

/// <summary>
///     Global image-correction API (gamma / contrast / brightness / per-channel white balance). Applies live
///     to the render loop's <see cref="ImageCorrectionService"/>, so it affects EVERY output mode (network,
///     gpio/hardware, hdmi, spi) and the web preview at once. Optionally persists to the "ImageCorrection"
///     section of appsettings.json. Also exposes the network panel's R/B wiring swap for convenience.
/// </summary>
public static class ImageCorrectionEndpoints
{
    private static readonly string ConfigPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    public static void MapImageCorrectionEndpoints(this WebApplication app, ImageCorrectionService correction,
        IMatrixRenderer renderer)
    {
        NetworkMatrixRenderer? CurrentNet() =>
            (renderer as OutputRuntime)?.As<NetworkMatrixRenderer>() ?? renderer as NetworkMatrixRenderer;

        app.MapGet("/api/settings/image-correction", () =>
        {
            try
            {
                var net = CurrentNet();
                return ApiResponse.Ok(new
                {
                    curve = correction.Curve,
                    gamma = correction.Gamma,
                    contrast = correction.Contrast,
                    brightness = correction.Brightness,
                    gainR = correction.GainR,
                    gainG = correction.GainG,
                    gainB = correction.GainB,
                    active = correction.Active,
                    swapSupported = net != null,
                    swapRedBlue = net?.SwapRedBlue ?? false
                });
            }
            catch (Exception ex)
            {
                return ApiResponse.Error(ex);
            }
        });

        // Body: { curve, gamma, contrast, brightness, gainR, gainG, gainB, swapRedBlue?, save? }
        app.MapPost("/api/settings/image-correction", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                var curve = GetString(root, "curve", correction.Curve);
                var gamma = Math.Clamp(GetDouble(root, "gamma", correction.Gamma), 0.1, 5.0);
                var contrast = Math.Clamp(GetDouble(root, "contrast", correction.Contrast), 0.1, 3.0);
                var brightness = Math.Clamp(GetDouble(root, "brightness", correction.Brightness), 0.1, 4.0);
                var gainR = Math.Clamp(GetDouble(root, "gainR", correction.GainR), 0.0, 4.0);
                var gainG = Math.Clamp(GetDouble(root, "gainG", correction.GainG), 0.0, 4.0);
                var gainB = Math.Clamp(GetDouble(root, "gainB", correction.GainB), 0.0, 4.0);
                var save = !root.TryGetProperty("save", out var s) || s.ValueKind != JsonValueKind.False;

                correction.Set(curve, gamma, contrast, brightness, gainR, gainG, gainB);

                var net = CurrentNet();
                bool? swap = null;
                if (root.TryGetProperty("swapRedBlue", out var sw) &&
                    (sw.ValueKind == JsonValueKind.True || sw.ValueKind == JsonValueKind.False))
                {
                    swap = sw.GetBoolean();
                    net?.SetSwapRedBlue(swap.Value);
                }

                if (save) Persist(correction, swap);

                Console.WriteLine($"[API] Image correction: curve={correction.Curve} gamma={correction.Gamma:0.###} " +
                                  $"contrast={correction.Contrast:0.###} bright={correction.Brightness:0.###} " +
                                  $"gain={correction.GainR:0.##}/{correction.GainG:0.##}/{correction.GainB:0.##} " +
                                  $"active={correction.Active} swap={(swap?.ToString() ?? "-")} save={save}");

                return ApiResponse.Ok(new
                {
                    curve = correction.Curve,
                    gamma = correction.Gamma,
                    contrast = correction.Contrast,
                    brightness = correction.Brightness,
                    gainR = correction.GainR,
                    gainG = correction.GainG,
                    gainB = correction.GainB,
                    active = correction.Active,
                    swapSupported = net != null,
                    swapRedBlue = net?.SwapRedBlue ?? false,
                    saved = save
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[API] Error setting image correction: {ex.Message}");
                return ApiResponse.Error(ex);
            }
        });
    }

    private static double GetDouble(JsonElement root, string name, double fallback) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var v)
            ? v : fallback;

    private static string GetString(JsonElement root, string name, string fallback) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? fallback : fallback;

    private static void Persist(ImageCorrectionService c, bool? swap)
    {
        if (!File.Exists(ConfigPath)) return;
        var node = JsonNode.Parse(File.ReadAllText(ConfigPath), null,
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        if (node == null) return;

        if (node["ImageCorrection"] is not JsonObject ic)
        {
            ic = new JsonObject();
            node["ImageCorrection"] = ic;
        }
        ic["Curve"] = c.Curve;
        ic["Gamma"] = c.Gamma;
        ic["Contrast"] = c.Contrast;
        ic["Brightness"] = c.Brightness;
        ic["GainR"] = c.GainR;
        ic["GainG"] = c.GainG;
        ic["GainB"] = c.GainB;

        if (swap.HasValue)
        {
            if (node["Network"] is not JsonObject netObj)
            {
                netObj = new JsonObject();
                node["Network"] = netObj;
            }
            netObj["SwapRedBlue"] = swap.Value;
        }

        File.Copy(ConfigPath, ConfigPath + ".backup", true);
        File.WriteAllText(ConfigPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
