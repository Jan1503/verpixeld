using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using verpixeld.Configuration;
using verpixeld.Services;
using verpixeld.WebApi;

namespace verpixeld.Tests.Endpoints;

/// <summary>
///     The real host with the real services and endpoint groups, in simulation output, listening on a free
///     loopback port. The render loop is not started (Program does that separately), so endpoints work on the
///     layout and stores directly. Data lands in the test's output folder (AppPaths): tests use names of their
///     own and remove what they create.
/// </summary>
public sealed class HostFixture : IAsyncLifetime
{
    private WebApplication? _app;

    public HttpClient Client { get; private set; } = null!;

    public IServiceProvider Services => _app!.Services;

    public async Task InitializeAsync()
    {
        AppPaths.EnsureDirectories();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["App:OutputMode"] = "simulation",
            ["App:SimulationMode"] = "true",
            ["App:DisplayWidth"] = "384",
            ["App:DisplayHeight"] = "192",
            ["HomeAssistant:Enabled"] = "false"
        });
        builder.Services.AddAppConfiguration(builder.Configuration);
        builder.Services.AddVerpixeldHost(builder.Configuration, new LogService());
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        _app = builder.Build();
        _app.MapDisplayEndpointsWithContext();
        _app.MapLayoutEndpoints();
        _app.MapScheduleEndpoints();
        _app.MapFavoritesEndpoints();
        _app.MapRadioEndpoints();
        await _app.StartAsync();

        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    // ── small helpers for the JSON the endpoints return ──

    public async Task<JsonElement> GetJson(string url) => await Read(await Client.GetAsync(url));

    public async Task<JsonElement> PostJson(string url, object? body = null) =>
        await Read(await Client.PostAsJsonAsync(url, body ?? new { }));

    public async Task<JsonElement> PutJson(string url, object body) => await Read(await Client.PutAsJsonAsync(url, body));

    public async Task<JsonElement> Delete(string url) => await Read(await Client.DeleteAsync(url));

    private static async Task<JsonElement> Read(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}

/// <summary>One host for every endpoint test class (the services are singletons with shared files).</summary>
[CollectionDefinition(Name)]
public sealed class HostCollection : ICollectionFixture<HostFixture>
{
    public const string Name = "Host";
}

internal static class JsonExtensions
{
    public static bool Success(this JsonElement e) => e.TryGetProperty("success", out var s) && s.GetBoolean();

    public static string? Str(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
