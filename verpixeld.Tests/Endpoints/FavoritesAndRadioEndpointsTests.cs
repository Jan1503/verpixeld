using System.Text.Json;

namespace verpixeld.Tests.Endpoints;

/// <summary>Favorites (list, rename, remove, duplicate rule, "is the current media a favorite") and radio stations.</summary>
[Collection(HostCollection.Name)]
public class FavoritesAndRadioEndpointsTests(HostFixture host)
{
    private async Task<List<JsonElement>> Favorites() =>
        (await host.GetJson("/api/favorites")).GetProperty("favorites").EnumerateArray().ToList();

    [Fact]
    public async Task Current_media_is_no_favorite_when_nothing_was_played()
    {
        var result = await host.GetJson("/api/favorites/current");

        Assert.True(result.Success());
        Assert.False(result.GetProperty("hasMedia").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("favoriteId").ValueKind);
    }

    [Fact]
    public async Task Favorite_add_rename_remove_and_no_duplicate_for_the_same_source()
    {
        var path = $"Videos/Bob's clip {Guid.NewGuid():N}.mp4";
        string? id = null;
        try
        {
            var added = await host.PostJson("/api/favorites", new { name = "First", type = 0, localPath = path });
            Assert.True(added.Success(), added.ToString());
            id = added.GetProperty("favorite").Str("id");
            Assert.False(string.IsNullOrEmpty(id));

            // Same source again: the existing entry is updated, not duplicated.
            var again = await host.PostJson("/api/favorites", new { name = "Second", type = 0, localPath = path });
            Assert.Equal(id, again.GetProperty("favorite").Str("id"));
            Assert.Single(await Favorites(), f => f.Str("id") == id);

            Assert.True((await host.Client.PutAsync($"/api/favorites/{id}?name={Uri.EscapeDataString("Jan's pick")}", null)).IsSuccessStatusCode);
            Assert.Equal("Jan's pick", (await Favorites()).Single(f => f.Str("id") == id).Str("name"));

            Assert.True((await host.Delete($"/api/favorites/{id}")).Success());
            Assert.DoesNotContain(await Favorites(), f => f.Str("id") == id);
            Assert.False((await host.Delete($"/api/favorites/{id}")).Success()); // already gone
            id = null;
        }
        finally
        {
            if (id != null) await host.Client.DeleteAsync($"/api/favorites/{id}");
        }
    }

    private async Task<List<JsonElement>> Stations() =>
        (await host.GetJson("/api/radio/stations")).GetProperty("stations").EnumerateArray().ToList();

    [Fact]
    public async Task Radio_station_add_rename_move_and_remove()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var ids = new List<string>();
        try
        {
            foreach (var n in new[] { $"Bob's FM {tag}", $"Zwei {tag}" })
            {
                var r = await host.PostJson("/api/radio/stations", new { name = n, url = $"http://192.0.2.1/{tag}/{ids.Count}" });
                Assert.True(r.Success(), r.ToString());
                ids.Add(r.GetProperty("station").Str("id")!);
            }

            var invalid = await host.PostJson("/api/radio/stations", new { name = "No URL", url = "" });
            Assert.False(invalid.Success());

            Assert.True((await host.PutJson($"/api/radio/stations/{ids[0]}", new { name = $"Bob's Radio {tag}" })).Success());
            Assert.Contains(await Stations(), s => s.Str("name") == $"Bob's Radio {tag}");

            var order = (await Stations()).Select(s => s.Str("id")).ToList();
            Assert.True(order.IndexOf(ids[0]) < order.IndexOf(ids[1]));
            Assert.True((await host.PostJson($"/api/radio/stations/{ids[0]}/move?delta=1")).Success());
            order = (await Stations()).Select(s => s.Str("id")).ToList();
            Assert.True(order.IndexOf(ids[0]) > order.IndexOf(ids[1]));
        }
        finally
        {
            foreach (var id in ids)
                Assert.True((await host.Delete($"/api/radio/stations/{id}")).Success());
        }
        Assert.DoesNotContain(await Stations(), s => ids.Contains(s.Str("id")!));
    }
}
