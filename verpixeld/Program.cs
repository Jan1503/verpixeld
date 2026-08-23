using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using verpixeld.Configuration;
using verpixeld.Hardware;
using verpixeld.Interfaces;
using verpixeld.Layout;
using verpixeld.MediaPlayer.Audio;
using verpixeld.Services;
using verpixeld.WebApi;

namespace verpixeld;

public class Program
{
    private static bool _verboseLogging;

    public static async Task Main(string[] args)
    {
        var logService = new LogService();
        logService.Install();

        ParseCommandLineArgs(args);
        AppPaths.EnsureDirectories();

        var builder = WebApplication.CreateBuilder();
        builder.Host.ConfigureHostOptions(opts => opts.ShutdownTimeout = TimeSpan.FromSeconds(2));
        builder.Services.AddAppConfiguration(builder.Configuration);
        builder.Services.AddVerpixeldHost(builder.Configuration, logService);

        var appConfig = builder.Configuration.GetSection(AppOptions.SectionName).Get<AppOptions>();
        var useVerboseLogging = _verboseLogging || (appConfig?.VerboseLogging ?? false);
        ConfigureLogging(builder, useVerboseLogging);

        var webServerOptions = builder.Configuration.GetSection(WebServerOptions.SectionName).Get<WebServerOptions>()
                               ?? new WebServerOptions();

        var certPath = webServerOptions.CertificatePath;
        var certPassword = webServerOptions.CertificatePassword;
        if (!string.IsNullOrEmpty(certPath) && !Path.IsPathRooted(certPath))
            certPath = Path.Combine(AppContext.BaseDirectory, certPath);

        var certService = new CertificateService(certPath, certPassword);
        builder.Services.AddSingleton(certService);

        System.Security.Cryptography.X509Certificates.X509Certificate2? certificate = null;
        if (webServerOptions.EnableHttps)
            certificate = certService.GetOrCreateCertificate();

        builder.WebHost.ConfigureKestrelEndpoints(
            certificate,
            webServerOptions.HttpPort,
            webServerOptions.HttpsPort,
            webServerOptions.EnableHttps);

        var app = builder.Build();
        HostRuntime.Start(app.Services);

        app.UseDisplayMiddleware(
            certificate,
            useVerboseLogging,
            webServerOptions.HttpsPort,
            webServerOptions.EnableHttps);
        app.UseDisplayStaticFiles();
        app.MapUtilityEndpoints();

        app.MapDisplayEndpointsWithContext();
        app.MapLayoutEndpoints();
        app.MapBrightnessEndpoints();
        app.MapNightModeEndpoints();
        app.MapScheduleEndpoints();
        app.MapMediaPlayerEndpoints();
        app.MapNetworkShareEndpoints();
        app.MapYouTubeEndpoints();
        app.MapFavoritesEndpoints();
        app.MapAudioEndpoints();
        app.MapVisualizerEndpoints();
        app.MapSettingsEndpoints();
        app.MapOutputSettingsEndpoints();
        app.MapImageCorrectionEndpoints();
        app.MapNetworkConfigEndpoints();
        app.MapSeamEndpoints();
        app.MapLogEndpoints();
        app.MapAlertEndpoints();
        app.MapAiEndpoints();
        app.MapVoiceEndpoints();
        app.MapMusicSearchEndpoints();
        app.MapPreviewEndpoints();
        app.MapNowPlayingEndpoints();
        app.MapHomeAssistantEndpoints();
        app.MapGeoEndpoints();
        app.MapPlaylistEndpoints();
        app.MapCanvasRotationEndpoints();

        HostRuntime.LogDiscovery(app.Services);

        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = async (context, report) =>
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new
                {
                    status = report.Status.ToString(),
                    totalDuration = report.TotalDuration.TotalMilliseconds,
                    checks = report.Entries.Select(e => new
                    {
                        name = e.Key,
                        status = e.Value.Status.ToString(),
                        description = e.Value.Description,
                        duration = e.Value.Duration.TotalMilliseconds,
                        data = e.Value.Data
                    })
                });
            }
        });

        app.MapGet("/", () => Results.Content(WebUIProvider.GetIndexHtml(), "text/html; charset=utf-8"));

        var localIp = StartupService.GetLocalIPAddress();
        Console.WriteLine("\n╔════════════════════════════════════════════════════════╗");
        Console.WriteLine("║  ╦  ╦╔═╗╦═╗╔═╗╦═╗ ╦╔═╗╦  ╔╦╗                           ║");
        Console.WriteLine("║  ╚╗╔╝║╣ ╠╦╝╠═╝║╔╩╦╝║╣ ║   ║║  LED Matrix Control       ║");
        Console.WriteLine("║   ╚╝ ╚═╝╩╚═╩  ╩╩ ╚═╚═╝╩═╝═╩╝                           ║");
        Console.WriteLine("╠════════════════════════════════════════════════════════╣");
        Console.WriteLine($"║  HTTP:  http://{localIp}:{webServerOptions.HttpPort}");
        if (webServerOptions.EnableHttps)
            Console.WriteLine($"║  HTTPS: https://{localIp}:{webServerOptions.HttpsPort}");
        Console.WriteLine("╚════════════════════════════════════════════════════════╝\n");

        var shutdownCts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nShutdown requested...");
            StopRuntime(app.Services);
            shutdownCts.Cancel();
        };

        await app.StartAsync();
        Console.WriteLine("[WEB] Listening — intro and default layout continue in the background");

        try
        {
            await app.Services.GetRequiredService<StartupService>().ShowIntroAsync();
            await app.Services.GetRequiredService<HostOrchestrator>().StartLocalModeAsync();
            await app.WaitForShutdownAsync(shutdownCts.Token);
        }
        finally
        {
            await app.StopAsync();
            Console.WriteLine("Application shutdown complete.");
        }
    }

    private static void StopRuntime(IServiceProvider sp)
    {
        try
        {
            sp.GetRequiredService<PulseAudioEventService>().StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SHUTDOWN] PulseAudio: {ex.Message}");
        }

        TryStop(() => sp.GetRequiredService<HealthMonitoringService>().Stop());
        TryStop(() => sp.GetRequiredService<VoiceCommandService>().Stop());
        TryStop(() => sp.GetRequiredService<HomeAssistantService>().Stop());
        TryStop(() => sp.GetRequiredService<HaWallDevice>().Dispose());
        TryStop(() => sp.GetRequiredService<HaToastService>().Dispose());
        TryStop(() => sp.GetRequiredService<ILayoutScheduleManager>().Stop());
        TryStop(() => sp.GetRequiredService<NightModeManager>().Dispose());
        TryStop(() => sp.GetRequiredService<AlertService>().Dispose());
        TryStop(() => sp.GetRequiredService<LocalCameraService>().Dispose());

        var render = sp.GetRequiredService<IRenderService>();
        TryStop(render.Stop);
        if (render is IDisposable d)
            TryStop(d.Dispose);

        Console.WriteLine("[SHUTDOWN] All services stopped");
    }

    private static void TryStop(Action action)
    {
        try { action(); }
        catch (Exception ex) { Console.WriteLine($"[SHUTDOWN] {ex.Message}"); }
    }

    private static void ParseCommandLineArgs(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
            switch (args[i].ToLower())
            {
                case "-v":
                case "--verbose":
                    _verboseLogging = true;
                    Console.WriteLine("[CONFIG] Verbose logging enabled");
                    break;
                case "-h":
                case "--help":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
            }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("verpixeld - LED Matrix Control System");
        Console.WriteLine();
        Console.WriteLine("Usage: verpixeld [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -v, --verbose    Enable verbose web server logging");
        Console.WriteLine("  -h, --help       Show this help message");
        Console.WriteLine();
        Console.WriteLine("Ports:");
        Console.WriteLine("  HTTP:  5000");
        Console.WriteLine("  HTTPS: 5001 (for camera streaming)");
        Console.WriteLine();
        Console.WriteLine("Configuration:");
        Console.WriteLine("  Settings can be configured in appsettings.json");
        Console.WriteLine();
        Console.WriteLine("Health Check:");
        Console.WriteLine("  GET /health - Returns system and display health status");
    }

    private static void ConfigureLogging(WebApplicationBuilder builder, bool verbose)
    {
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.SetMinimumLevel(verbose ? LogLevel.Information : LogLevel.Error);
        if (verbose)
            Console.WriteLine("[CONFIG] Verbose logging enabled");
    }
}
