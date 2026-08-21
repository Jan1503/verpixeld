using System.Text.Json;
using System.Text.Json.Serialization;
using PixPlane;
using verpixeld.Hardware;

namespace verpixeld.WebApi;

/// <summary>
///     Per-column seam correction (driver-cascade boundary columns, e.g. 63/127/191/255).
///     Live when the network output is active; otherwise reads/writes seam_correction.json so the
///     values are ready the next time that output is selected.
/// </summary>
public static class SeamEndpoints
{
    private static readonly string DefaultSeamFile = Path.Combine(AppContext.BaseDirectory, "seam_correction.json");

    private static readonly JsonSerializerOptions SeamJsonRead = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions SeamJsonWrite = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void MapSeamEndpoints(this WebApplication app, IMatrixRenderer renderer)
    {
        NetworkMatrixRenderer? Current() =>
            (renderer as OutputRuntime)?.As<NetworkMatrixRenderer>() ?? renderer as NetworkMatrixRenderer;

        app.MapGet("/api/settings/seam", () =>
        {
            try
            {
                var net = Current();
                var src = net != null ? net.GetSeamColumns() : LoadSeamFromFile();
                var preview = net?.SeamPreviewGrey ?? -1;
                return ApiResponse.Ok(new
                {
                    supported = true,
                    columns = src.Select(ToDto),
                    live = net != null,
                    previewLevel = preview
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
                if (net != null) net.SetSeam(list, save);
                else if (save) WriteSeamFile(list);

                Console.WriteLine($"[API] Seam correction: {list.Count} columns, save={save}, live={net != null}");

                return ApiResponse.Ok(new
                {
                    supported = true,
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

    private sealed class SeamConfig { public List<SeamColumn> Columns { get; set; } = []; }

    private static List<SeamColumn> LoadSeamFromFile()
    {
        try
        {
            if (!File.Exists(DefaultSeamFile)) return DefaultColumns();
            var cfg = JsonSerializer.Deserialize<SeamConfig>(File.ReadAllText(DefaultSeamFile), SeamJsonRead);
            return cfg?.Columns is { Count: > 0 } cols ? cols : DefaultColumns();
        }
        catch { return DefaultColumns(); }
    }

    private static void WriteSeamFile(IReadOnlyList<SeamColumn> cols)
    {
        var cfg = new SeamConfig { Columns = cols.ToList() };
        File.WriteAllText(DefaultSeamFile, JsonSerializer.Serialize(cfg, SeamJsonWrite));
    }

    private static List<SeamColumn> DefaultColumns()
    {
        SeamColumn Col(int x) => new()
        {
            X = x, GainR = 0.85, GainG = 0.85, GainB = 0.85, LiftR = 0.004, LiftG = 0.004, LiftB = 0.004
        };
        return [Col(63), Col(127), Col(191), Col(255)];
    }
}
