using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using verpixeld.MediaPlayer;
using verpixeld.Services;

namespace verpixeld.WebApi;

/// <summary>Internet radio: saved stations, Radio Browser search, now playing, sleep timer.</summary>
public static class RadioEndpoints
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public sealed class StationBody
    {
        public string? Name { get; set; }
        public string? Url { get; set; }
        public string? Favicon { get; set; }
        public string? Homepage { get; set; }
        public string? Tags { get; set; }
        public string? Country { get; set; }
        public string? CountryCode { get; set; }
        public string? Language { get; set; }
        public string? Codec { get; set; }
        public int Bitrate { get; set; }
        public string? StationUuid { get; set; }

        public RadioStationEntry ToEntry() => new()
        {
            Name = Name ?? "",
            Url = Url ?? "",
            Favicon = Favicon ?? "",
            Homepage = Homepage ?? "",
            Tags = Tags ?? "",
            Country = Country ?? "",
            CountryCode = CountryCode ?? "",
            Language = Language ?? "",
            Codec = Codec ?? "",
            Bitrate = Bitrate,
            StationUuid = StationUuid ?? ""
        };
    }

    public sealed class OrderBody
    {
        public List<string> Ids { get; set; } = new();
    }

    private static object View(RadioStationEntry s) => new
    {
        s.Id, s.Name, s.Url, s.Favicon, s.Homepage, s.Tags, s.Country, s.CountryCode, s.Language,
        s.Codec, s.Bitrate, s.StationUuid, s.AddedAt, s.LastPlayedAt, s.PlayCount
    };

    private static object View(RadioStation s) => new
    {
        s.Name, Url = s.StreamUrl, s.Favicon, s.Homepage, s.Tags, s.Country, s.CountryCode, s.Language,
        s.Codec, s.Bitrate, s.StationUuid, s.Votes, s.ClickCount
    };

    public static void MapRadioEndpoints(this WebApplication app)
    {
        var radio = app.Services.GetRequiredService<RadioService>();
        var browser = app.Services.GetRequiredService<RadioBrowserService>();
        var sleep = app.Services.GetRequiredService<SleepTimerService>();
        var group = app.MapGroup("/api/radio");

        group.MapGet("/stations", () =>
            Results.Json(new { success = true, stations = radio.Stations.Select(View), current = radio.CurrentIndex }));

        group.MapPost("/stations", (StationBody body) =>
        {
            try
            {
                var s = radio.Add(body.ToEntry());
                return Results.Json(new { success = true, station = View(s) });
            }
            catch (ArgumentException ex)
            {
                return Results.Json(new { success = false, message = ex.Message });
            }
        });

        group.MapPut("/stations/{id}", (string id, StationBody body) =>
        {
            try
            {
                var ok = radio.Update(id, body.Name, body.Url, body.Favicon, body.Tags);
                return Results.Json(new { success = ok, message = ok ? null : "Station not found" });
            }
            catch (ArgumentException ex)
            {
                return Results.Json(new { success = false, message = ex.Message });
            }
        });

        group.MapDelete("/stations/{id}", (string id) =>
            Results.Json(new { success = radio.Remove(id) }));

        group.MapPost("/stations/{id}/move", (string id, int delta) =>
            Results.Json(new { success = radio.Move(id, delta) }));

        group.MapPost("/stations/order", (OrderBody body) =>
        {
            radio.Reorder(body.Ids);
            return Results.Json(new { success = true });
        });

        group.MapPost("/stations/{id}/play", async (string id) =>
            Results.Json(new { success = await radio.PlayAsync(id) }));

        // Play a station that is not saved (search result, recent entry).
        group.MapPost("/play", async (StationBody body) =>
        {
            try
            {
                return Results.Json(new { success = await radio.PlayAsync(body.ToEntry()) });
            }
            catch (ArgumentException ex)
            {
                return Results.Json(new { success = false, message = ex.Message });
            }
        });

        group.MapPost("/next", async () => Results.Json(new { success = await radio.StepAsync(1) }));
        group.MapPost("/previous", async () => Results.Json(new { success = await radio.StepAsync(-1) }));

        group.MapPost("/stop", async () =>
        {
            await radio.StopAsync();
            return Results.Json(new { success = true });
        });

        group.MapGet("/now", () =>
        {
            var n = radio.Now;
            return Results.Json(new
            {
                success = true,
                playing = n.Playing,
                saved = n.Saved,
                station = n.Station == null ? null : View(n.Station),
                streamTitle = n.StreamTitle,
                titleSince = n.TitleSince,
                icyName = n.IcyName,
                icyGenre = n.IcyGenre,
                icyBitrate = n.IcyBitrate,
                startedAt = n.StartedAt,
                titles = n.Titles.Select(t => new { t.Title, t.At }),
                readTitles = radio.ReadTitles,
                sleepMinutes = sleep.MinutesLeft,
                sleepUntil = sleep.Until
            });
        });

        group.MapPost("/titles", (bool enabled) =>
        {
            radio.ReadTitles = enabled;
            return Results.Json(new { success = true, readTitles = radio.ReadTitles });
        });

        group.MapGet("/recent", () => Results.Json(new { success = true, stations = radio.Recent.Select(View) }));

        group.MapDelete("/recent", () =>
        {
            radio.ClearRecent();
            return Results.Json(new { success = true });
        });

        group.MapGet("/search", async (string? name, string? tag, string? country, string? order, int? limit,
            int? offset, CancellationToken ct) =>
        {
            var list = await browser.SearchAsync(name, tag, country, order, limit ?? 40, offset ?? 0, ct);
            return Results.Json(new { success = true, stations = list.Select(View) });
        });

        group.MapGet("/tags", async (CancellationToken ct) =>
        {
            var tags = await browser.TopTagsAsync(80, ct);
            return Results.Json(new { success = true, tags = tags.Select(t => new { name = t.Name, count = t.Count }) });
        });

        group.MapGet("/countries", async (CancellationToken ct) =>
        {
            var list = await browser.CountriesAsync(ct);
            return Results.Json(new
            {
                success = true,
                countries = list.Select(c => new { code = c.Code, name = c.Name, count = c.Count })
            });
        });

        group.MapGet("/export", () =>
            Results.File(System.Text.Encoding.UTF8.GetBytes(radio.Export()), "application/json", "radio-stations.json"));

        group.MapPost("/import", async (HttpRequest request, bool? replace) =>
        {
            try
            {
                var list = await JsonSerializer.DeserializeAsync<List<RadioStationEntry>>(request.Body, Web)
                           ?? new List<RadioStationEntry>();
                var added = radio.Import(list, replace ?? false);
                return Results.Json(new { success = true, added });
            }
            catch (JsonException ex)
            {
                return Results.Json(new { success = false, message = "Not a station list: " + ex.Message });
            }
        });

        // ---- alarm clock ----------------------------------------------------------------
        var alarms = app.Services.GetRequiredService<AlarmService>();
        object AlarmView(AlarmEntry a) => new
        {
            a.Id, a.Enabled, a.Hour, a.Minute, a.Days, a.StationId, a.Volume, a.FadeMinutes, a.Label,
            next = AlarmService.NextFire(a, AlarmService.LocalNow())
        };
        app.MapGet("/api/alarms", () => Results.Json(new
        {
            success = true,
            alarms = alarms.Alarms.Select(AlarmView),
            ringing = alarms.Ringing?.Id,
            now = AlarmService.LocalNow()
        }));
        app.MapPost("/api/alarms", (AlarmEntry body) =>
            Results.Json(new { success = true, alarm = AlarmView(alarms.Save(body)) }));
        app.MapDelete("/api/alarms/{id}", (string id) => Results.Json(new { success = alarms.Remove(id) }));
        app.MapPost("/api/alarms/{id}/test", async (string id) =>
        {
            await alarms.TestAsync(id);
            return Results.Json(new { success = true });
        });
        app.MapPost("/api/alarms/stop", () =>
        {
            alarms.StopRinging();
            return Results.Json(new { success = true });
        });

        // ---- songs: heard on the radio, remembered ----------------------------------------
        var songs = app.Services.GetRequiredService<SongLogService>();
        app.MapGet("/api/songs", () => Results.Json(new
        {
            success = true,
            current = songs.Current() is { } c ? new { title = c.Title, source = c.Source } : null,
            history = songs.History.Take(100),
            saved = songs.SavedSongs
        }));
        app.MapPost("/api/songs/save", () =>
        {
            var e = songs.SaveCurrent();
            return Results.Json(new { success = e != null, song = e, message = e == null ? "Nothing with a title is playing" : null });
        });
        app.MapPost("/api/songs/saved", (SongEntry body) =>
            string.IsNullOrWhiteSpace(body.Title)
                ? Results.Json(new { success = false, message = "Title required" })
                : Results.Json(new { success = true, song = songs.Save(body.Title, body.Source) }));
        app.MapDelete("/api/songs/saved/{id}", (string id) => Results.Json(new { success = songs.RemoveSaved(id) }));
        app.MapDelete("/api/songs/history", () =>
        {
            songs.ClearHistory();
            return Results.Json(new { success = true });
        });

        // Sleep timer (also on the audio board's MORE page).
        app.MapGet("/api/sleep", () => Results.Json(new { success = true, minutes = sleep.MinutesLeft, until = sleep.Until }));
        app.MapPost("/api/sleep", (int minutes) =>
        {
            sleep.Set(Math.Max(0, minutes));
            return Results.Json(new { success = true, minutes = sleep.MinutesLeft, until = sleep.Until });
        });
    }
}
