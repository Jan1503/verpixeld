using System.Text.Json;

namespace verpixeld.WebApi;

/// <summary>
///     Receives "now playing" media metadata (pushed by a companion agent on your PC) and persists it to a
///     small file the Now-Playing display extension reads. Decoupled on purpose: the agent only needs HTTP,
///     and the plugin only needs the file - no shared assemblies.
/// </summary>
public static class NowPlayingEndpoints
{
    public static void MapNowPlayingEndpoints(this WebApplication app)
    {
        app.MapPost("/api/nowplaying", async context =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                string Str(string n) =>
                    root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                double Num(string n) =>
                    root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
                        ? d
                        : 0.0;
                bool Bool(string n) => root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;

                Directory.CreateDirectory(NowPlayingPaths.Dir);

                var artFile = "";
                if (root.TryGetProperty("artBase64", out var ab) && ab.ValueKind == JsonValueKind.String)
                {
                    var b64 = ab.GetString();
                    if (!string.IsNullOrWhiteSpace(b64))
                        try
                        {
                            await File.WriteAllBytesAsync(Path.Combine(NowPlayingPaths.Dir, "art.png"),
                                Convert.FromBase64String(b64));
                            artFile = "art.png";
                        }
                        catch
                        {
                            // ignore malformed art
                        }
                }

                var outObj = new
                {
                    title = Str("title"),
                    artist = Str("artist"),
                    album = Str("album"),
                    isPlaying = Bool("isPlaying"),
                    position = Num("positionSeconds"),
                    duration = Num("durationSeconds"),
                    art = artFile,
                    updatedUtc = DateTime.UtcNow.ToString("o")
                };

                await File.WriteAllTextAsync(Path.Combine(NowPlayingPaths.Dir, "current.json"),
                    JsonSerializer.Serialize(outObj));

                await context.Response.WriteAsJsonAsync(new { success = true });
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { success = false, error = ex.Message });
            }
        });

        app.MapGet("/api/nowplaying", () =>
        {
            var f = Path.Combine(NowPlayingPaths.Dir, "current.json");
            return File.Exists(f)
                ? Results.Text(File.ReadAllText(f), "application/json")
                : Results.Json(new { success = false });
        });

        app.MapGet("/api/nowplaying/art", () =>
        {
            var f = Path.Combine(NowPlayingPaths.Dir, "art.png");
            return File.Exists(f) ? Results.File(File.ReadAllBytes(f), "image/png") : Results.NotFound();
        });
    }
}

/// <summary>Shared location for the now-playing snapshot (read by the display plugin in the same process).</summary>
public static class NowPlayingPaths
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "nowplaying");
}
