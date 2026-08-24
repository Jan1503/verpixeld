using System.Text.Json;
using PixPlane;
using verpixeld.Configuration;
using verpixeld.Hardware;

namespace verpixeld.WebApi;

/// <summary>
///     Per-column seam correction (driver-cascade boundary columns, e.g. 63/127/191/255).
///     Two profiles (8-bit / 14-bit). Live when the network output is active; otherwise
///     reads/writes seam_correction.json so the values are ready the next time that output is selected.
/// </summary>
public static class SeamEndpoints
{
    private static string DefaultSeamFile => AppPaths.ResolveWritableConfigFile(null, "seam_correction.json");

    public static void MapSeamEndpoints(this WebApplication app)
    {
        var renderer = app.Services.GetRequiredService<IMatrixRenderer>();
        NetworkMatrixRenderer? Current() =>
            (renderer as OutputRuntime)?.As<NetworkMatrixRenderer>() ?? renderer as NetworkMatrixRenderer;

        app.MapGet("/api/settings/seam", (int? bits) =>
        {
            try
            {
                var net = Current();
                var store = net == null ? SeamCorrectionStore.Load(DefaultSeamFile) : null;
                var bits8 = net?.GetSeamColumns(8) ?? store!.Get(8);
                var bits14 = net?.GetSeamColumns(14) ?? store!.Get(14);
                var liveBits = net?.ColorBits ?? 14;
                var lockBits = net?.SeamCalibrateBits ?? 0;
                var active = bits is 8 or 14
                    ? bits.Value
                    : lockBits is 8 or 14 ? lockBits : liveBits;
                var src = SeamCorrectionStore.NormalizeBits(active) == 14 ? bits14 : bits8;
                return ApiResponse.Ok(new
                {
                    supported = true,
                    bits = liveBits,
                    calibrateBits = lockBits,
                    columns = src.Select(ToDto),
                    profiles = new Dictionary<string, object>
                    {
                        ["8"] = new { columns = bits8.Select(ToDto) },
                        ["14"] = new { columns = bits14.Select(ToDto) }
                    },
                    live = net != null,
                    previewLevel = net?.SeamPreviewGrey ?? -1
                });
            }
            catch (Exception ex) { return ApiResponse.Error(ex); }
        });

        app.MapPost("/api/settings/seam", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                using var doc = JsonDocument.Parse(await reader.ReadToEndAsync());
                var root = doc.RootElement;
                var save = !root.TryGetProperty("save", out var s) || s.ValueKind != JsonValueKind.False;
                var bits = ReadBits(root);

                var list = new List<SeamColumn>();
                if (root.TryGetProperty("columns", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in arr.EnumerateArray())
                    {
                        var x = (int)GetD(e, "x", -1);
                        if (x < 0 || x >= 256) continue;
                        list.Add(ParseColumn(e, x));
                    }
                }

                var net = Current();
                var profileBits = bits ?? net?.SeamCalibrateBits switch
                {
                    8 or 14 => net.SeamCalibrateBits,
                    _ => net?.ColorBits
                } ?? 14;

                if (net != null) net.SetSeam(list, save, profileBits);
                else if (save)
                {
                    var store = File.Exists(DefaultSeamFile)
                        ? SeamCorrectionStore.Load(DefaultSeamFile)
                        : new SeamCorrectionStore();
                    store.Set(profileBits, list);
                    store.Save(DefaultSeamFile);
                }

                Console.WriteLine(
                    $"[API] Seam correction: {list.Count} columns, {SeamCorrectionStore.NormalizeBits(profileBits)}-bit, save={save}, live={net != null}");

                return ApiResponse.Ok(new
                {
                    supported = true,
                    bits = net?.ColorBits ?? profileBits,
                    calibrateBits = net?.SeamCalibrateBits ?? 0,
                    columns = list.Select(ToDto),
                    saved = save,
                    live = net != null,
                    previewLevel = net?.SeamPreviewGrey ?? -1
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[API] Error setting seam correction: {ex.Message}");
                return ApiResponse.Error(ex);
            }
        });

        app.MapPost("/api/settings/seam/mode", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                using var doc = JsonDocument.Parse(await reader.ReadToEndAsync());
                var bits = ReadBits(doc.RootElement) ?? 0;

                var net = Current();
                if (net == null)
                    return ApiResponse.Ok(new
                    {
                        supported = false,
                        live = false,
                        bits = 0,
                        calibrateBits = 0,
                        columns = Array.Empty<object>()
                    });

                net.SetSeamCalibrateBits(bits);
                if (bits is 8 or 14)
                    await net.EnsureColorBitsAsync(bits).ConfigureAwait(false);
                Console.WriteLine($"[API] Seam calibrate lock: {(bits is 8 or 14 ? bits + "-bit" : "off")} (panel {net.ColorBits}-bit)");

                return ApiResponse.Ok(new
                {
                    supported = true,
                    live = true,
                    bits = net.ColorBits,
                    calibrateBits = net.SeamCalibrateBits,
                    columns = net.GetSeamColumns(bits is 8 or 14 ? bits : null).Select(ToDto),
                    previewLevel = net.SeamPreviewGrey
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[API] Error setting seam mode: {ex.Message}");
                return ApiResponse.Error(ex);
            }
        });

        app.MapPost("/api/settings/seam/preview", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                using var doc = JsonDocument.Parse(await reader.ReadToEndAsync());
                var root = doc.RootElement;
                var level = -1;
                if (root.TryGetProperty("level", out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n))
                    level = n;

                var net = Current();
                if (net == null)
                    return ApiResponse.Ok(new { supported = false, live = false, previewLevel = -1 });

                net.SetSeamPreview(level);
                Console.WriteLine($"[API] Seam grey preview: {(level < 0 ? "off" : level.ToString())}");
                return ApiResponse.Ok(new { supported = true, live = true, previewLevel = net.SeamPreviewGrey });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[API] Error setting seam preview: {ex.Message}");
                return ApiResponse.Error(ex);
            }
        });
    }

    private static int? ReadBits(JsonElement root)
    {
        if (!root.TryGetProperty("bits", out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n))
            return n is 8 or 14 ? n : n == 0 ? 0 : null;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var s))
            return s is 8 or 14 ? s : s == 0 ? 0 : null;
        return null;
    }

    private static object ToDto(SeamColumn c) => new
    {
        x = c.X,
        gainR = c.GainR,
        gainG = c.GainG,
        gainB = c.GainB,
        liftR = c.LiftR,
        liftG = c.LiftG,
        liftB = c.LiftB,
        knots = c.SampleUiKnots().Select(k => new { @in = k.In, @out = k.Out })
    };

    private static SeamColumn ParseColumn(JsonElement e, int x)
    {
        var col = new SeamColumn
        {
            X = x,
            GainR = Clamp(GetD(e, "gainR", 1.0)),
            GainG = Clamp(GetD(e, "gainG", 1.0)),
            GainB = Clamp(GetD(e, "gainB", 1.0)),
            LiftR = ClampLift(GetD(e, "liftR", 0.0)),
            LiftG = ClampLift(GetD(e, "liftG", 0.0)),
            LiftB = ClampLift(GetD(e, "liftB", 0.0)),
            Knots = ReadKnots(e),
            Lut = ReadLut(e, "lut"),
            LutR = ReadLut(e, "lutR"),
            LutG = ReadLut(e, "lutG"),
            LutB = ReadLut(e, "lutB")
        };
        return col;
    }

    private static List<SeamKnot>? ReadKnots(JsonElement e)
    {
        if (!e.TryGetProperty("knots", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var list = new List<SeamKnot>();
        foreach (var k in arr.EnumerateArray())
        {
            list.Add(new SeamKnot
            {
                In = (int)GetD(k, "in", 0),
                Out = (int)GetD(k, "out", 0)
            });
        }
        return list.Count > 0 ? list : null;
    }

    private static int[]? ReadLut(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var n = arr.GetArrayLength();
        if (n != 256) return null;
        var lut = new int[256];
        var i = 0;
        foreach (var v in arr.EnumerateArray())
        {
                var val = i;
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n32))
                    val = Math.Clamp(n32, 0, 255);
                lut[i++] = val;
        }
        return lut;
    }

    private static double Clamp(double v) => v < 0 ? 0 : v > 4 ? 4 : v;
    private static double ClampLift(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    private static double GetD(JsonElement e, string name, double fallback) =>
        e.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var v) ? v : fallback;
}
