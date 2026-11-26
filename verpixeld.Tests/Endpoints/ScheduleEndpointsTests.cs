using System.Text.Json;

namespace verpixeld.Tests.Endpoints;

/// <summary>Schedules through the API: save, read back, list, delete; names with quotes.</summary>
[Collection(HostCollection.Name)]
public class ScheduleEndpointsTests(HostFixture host)
{
    private async Task<List<string>> Names() =>
        (await host.GetJson("/api/schedule/list")).GetProperty("data").EnumerateArray()
        .Select(s => s.Str("name") ?? "").ToList();

    [Fact]
    public async Task Save_read_and_delete_a_disabled_schedule()
    {
        var name = $"Abend's Plan {Guid.NewGuid():N}"[..21];
        try
        {
            var schedule = new
            {
                Name = name,
                Enabled = false, // never fires during the test
                Entries = new[] { new { LayoutName = "Scene", Time = "21:30", Enabled = true } }
            };
            var saved = await host.PostJson("/api/schedule/save", schedule);
            Assert.True(saved.Success(), saved.ToString());
            Assert.Contains(name, await Names());

            var read = await host.GetJson($"/api/schedule/{Uri.EscapeDataString(name)}");
            Assert.True(read.Success());
            var data = read.GetProperty("data");
            Assert.Equal(name, data.Str("name"));
            Assert.False(data.GetProperty("enabled").GetBoolean());
            Assert.Equal(1, data.GetProperty("entries").GetArrayLength());

            Assert.True((await host.Delete($"/api/schedule/{Uri.EscapeDataString(name)}")).Success());
            Assert.DoesNotContain(name, await Names());
        }
        finally
        {
            await host.Client.DeleteAsync($"/api/schedule/{Uri.EscapeDataString(name)}");
        }
    }

    [Fact]
    public async Task Reading_an_unknown_schedule_fails()
    {
        var result = await host.GetJson($"/api/schedule/{Guid.NewGuid():N}");

        Assert.False(result.Success());
    }

    [Fact]
    public async Task Saving_garbage_is_refused()
    {
        var response = await host.Client.PostAsync("/api/schedule/save", new StringContent("null"));
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.False(body.Success());
    }
}
