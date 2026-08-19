using System.Diagnostics;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace verpixeld.Configuration;

/// <summary>
///     Extension methods for configuring the web server and middleware
/// </summary>
public static class WebServerExtensions
{
    /// <summary>
    ///     Configures Kestrel for HTTP and HTTPS
    /// </summary>
    public static void ConfigureKestrelEndpoints(
        this ConfigureWebHostBuilder webHost,
        X509Certificate2? certificate,
        int httpPort = 5000,
        int httpsPort = 5001,
        bool enableHttps = true)
    {
        webHost.ConfigureKestrel(serverOptions =>
        {
            // Allow large file uploads (500MB) for demo videos
            serverOptions.Limits.MaxRequestBodySize = 500 * 1024 * 1024; // 500MB

            // HTTP on specified port (HTTP/1.1 only to avoid warning)
            serverOptions.ListenAnyIP(httpPort, listenOptions => { listenOptions.Protocols = HttpProtocols.Http1; });
            Console.WriteLine($"[SERVER] HTTP enabled on port {httpPort}");
            Console.WriteLine("[SERVER] Max upload size: 500MB");

            if (!enableHttps)
            {
                Console.WriteLine("[SERVER] HTTPS disabled via configuration (WebServer:EnableHttps = false)");
                return;
            }

            // HTTPS if certificate available
            if (certificate != null && certificate.HasPrivateKey)
                try
                {
                    serverOptions.ListenAnyIP(httpsPort, listenOptions =>
                    {
                        listenOptions.Protocols = HttpProtocols.Http1;
                        listenOptions.UseHttps(httpsOptions =>
                        {
                            httpsOptions.ServerCertificate = certificate;
                            httpsOptions.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                            httpsOptions.ClientCertificateMode = ClientCertificateMode.NoCertificate;
                        });
                    });
                    Console.WriteLine($"[SERVER] HTTPS enabled on port {httpsPort} (TLS 1.2/1.3)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SERVER] Warning: Could not enable HTTPS: {ex.Message}");
                }
            else
                Console.WriteLine("[SERVER] HTTPS NOT enabled - certificate missing or has no private key");
        });
    }

    /// <summary>
    ///     Adds standard middleware for the display API
    /// </summary>
    public static WebApplication UseDisplayMiddleware(
        this WebApplication app,
        X509Certificate2? certificate,
        bool verboseLogging,
        int httpsPort = 5001,
        bool enableHttpsRedirect = true)
    {
        // Global exception handler
        app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Unhandled exception: {ex.Message}");
                Console.WriteLine($"[ERROR] Stack: {ex.StackTrace}");
                context.Response.StatusCode = 500;
                await context.Response.WriteAsJsonAsync(new { success = false, error = ex.Message });
            }
        });

        // Request timing (only log slow requests)
        app.Use(async (context, next) =>
        {
            var sw = Stopwatch.StartNew();
            await next();
            sw.Stop();

            if (verboseLogging && sw.ElapsedMilliseconds > 500)
                Console.WriteLine(
                    $"[SLOW] {context.Request.Method} {context.Request.Path} took {sw.ElapsedMilliseconds}ms");
        });

        // HTTP to HTTPS redirect (only when HTTPS is actually enabled)
        if (enableHttpsRedirect && certificate != null)
            app.Use(async (context, next) =>
            {
                if (context.Request.Scheme == "http")
                {
                    // Allow HTTP if explicitly requested
                    var acceptHttp = context.Request.Headers.ContainsKey("X-Allow-HTTP");

                    if (!acceptHttp)
                    {
                        var httpsUrl =
                            $"https://{context.Request.Host.Host}:{httpsPort}{context.Request.Path}{context.Request.QueryString}";
                        context.Response.Redirect(httpsUrl, false);
                        return;
                    }
                }

                await next();
            });

        // Verbose request logging
        if (verboseLogging)
            app.Use(async (context, next) =>
            {
                Console.WriteLine($"[REQUEST] {context.Request.Scheme}://{context.Request.Host}{context.Request.Path}");
                await next();
            });

        // CORS
        app.Use(async (context, next) =>
        {
            context.Response.Headers.Append("Access-Control-Allow-Origin", "*");
            context.Response.Headers.Append("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE");
            context.Response.Headers.Append("Access-Control-Allow-Headers", "Content-Type");

            if (context.Request.Method == "OPTIONS")
            {
                context.Response.StatusCode = 200;
                return;
            }

            await next();
        });

        return app;
    }

    /// <summary>
    ///     Configures static file serving from wwwroot
    /// </summary>
    public static WebApplication UseDisplayStaticFiles(this WebApplication app)
    {
        var wwwrootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot");

        if (Directory.Exists(wwwrootPath))
        {
            var provider = new FileExtensionContentTypeProvider();
            provider.Mappings[".js"] = "application/javascript; charset=utf-8";
            provider.Mappings[".css"] = "text/css; charset=utf-8";
            provider.Mappings[".html"] = "text/html; charset=utf-8";

            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(wwwrootPath),
                RequestPath = "",
                ContentTypeProvider = provider
            });

            Console.WriteLine($"[SERVER] Static files: {wwwrootPath}");
        }
        else
        {
            Console.WriteLine($"[SERVER] Warning: wwwroot not found at {wwwrootPath}");
        }

        return app;
    }

    /// <summary>
    ///     Maps utility endpoints (test, GC)
    /// </summary>
    public static WebApplication MapUtilityEndpoints(this WebApplication app)
    {
        // Simple test endpoint for HTTPS verification
        app.MapGet("/test", () => Results.Text("HTTPS is working!"));

        // GC endpoint
        app.MapPost("/api/gc/run", () =>
        {
            var before = GC.GetTotalMemory(false) / 1024 / 1024;
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
            var after = GC.GetTotalMemory(true) / 1024 / 1024;

            return Results.Json(new { success = true, data = new { beforeMb = before, afterMb = after } });
        });

        return app;
    }
}
