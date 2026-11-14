using System.Net;
using System.Security.Cryptography.X509Certificates;
using Jellyfin.Plugin.ShapeShifter.Auth;
using Jellyfin.Plugin.ShapeShifter.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShapeShifter.Server;

/// <summary>
/// Hosts a separate Kestrel instance on the Plex port. Plex paths collide with Jellyfin's own
/// routes and clients expect port 32400 (and the plex.direct certificate), so this can't live
/// inside Jellyfin's ASP.NET pipeline.
/// </summary>
public sealed class PlexListenerService : IHostedService, IAsyncDisposable
{
    private readonly PlexRouter _router = new();
    private readonly PlexAuthService _auth;
    private readonly ILogger<PlexListenerService> _logger;
    private readonly SemaphoreSlim _restartLock = new(1, 1);
    private WebApplication? _app;
    private CertificateStore? _certificates;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _unhandled = new();

    public PlexListenerService(IEnumerable<IPlexEndpoints> endpoints, PlexAuthService auth, ILogger<PlexListenerService> logger)
    {
        _auth = auth;
        _logger = logger;
        var builder = _router.Builder;
        foreach (var group in endpoints)
        {
            group.Map(builder);
        }
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    /// <summary>Gets a value indicating whether the listener is accepting connections.</summary>
    public bool IsRunning => _app is not null;

    public CertificateStore? Certificates => _certificates;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged += OnConfigurationChanged;
        }

        await RestartAsync().ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged -= OnConfigurationChanged;
        }

        await StopListenerAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopListenerAsync().ConfigureAwait(false);
        _certificates?.Dispose();
        _restartLock.Dispose();
    }

    private async void OnConfigurationChanged(object? sender, MediaBrowser.Model.Plugins.BasePluginConfiguration e)
    {
        try
        {
            await RestartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart Plex listener after configuration change");
        }
    }

    private async Task RestartAsync()
    {
        await _restartLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopListenerAsync().ConfigureAwait(false);
            _auth.ClearCache();

            var config = Config;
            var address = IPAddress.TryParse(config.BindAddress, out var a) ? a : IPAddress.Any;
            _certificates?.Dispose();
            var certs = CertificateStore.Load(config, _logger);
            _certificates = certs;

            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                kestrel.AddServerHeader = false;
                kestrel.Limits.MaxRequestBodySize = 64 * 1024 * 1024;
                if (config.HttpPort > 0)
                {
                    kestrel.Listen(address, config.HttpPort, o =>
                    {
                        if (certs.Any)
                        {
                            UseDualMode(o, https => https.ServerCertificateSelector = (_, host) => certs.Select(host));
                        }
                    });
                }

                if (certs.Any && config.HttpsPort > 0 && config.HttpsPort != config.HttpPort)
                {
                    kestrel.Listen(address, config.HttpsPort, o => o.UseHttps(https => https.ServerCertificateSelector = (_, host) => certs.Select(host)));
                }
            });

            var app = builder.Build();
            app.UseWebSockets();
            ((IApplicationBuilder)app).Run(HandleAsync);
            await app.StartAsync().ConfigureAwait(false);
            _app = app;
            _logger.LogInformation(
                "Plex listener started on {Address}:{Port} (TLS {Tls}) as '{Name}' ({MachineId})",
                address,
                config.HttpPort,
                certs.Any ? "on" : "off",
                config.FriendlyName,
                config.MachineIdentifier);
        }
        finally
        {
            _restartLock.Release();
        }
    }

    /// <summary>
    /// PMS serves HTTP and HTTPS on the same port, and plex.tv advertises
    /// https://*.plex.direct:32400 connections, so we must too. Peek at the first byte: a TLS
    /// ClientHello starts with 0x16, anything else is plain HTTP and skips the TLS middleware.
    /// </summary>
    internal static void UseDualMode(ListenOptions listen, X509Certificate2 cert) =>
        UseDualMode(listen, https => https.ServerCertificate = cert);

    internal static void UseDualMode(ListenOptions listen, Action<HttpsConnectionAdapterOptions> configureHttps)
    {
        ConnectionDelegate? plainHttp = null;
        listen.Use(next => async connection =>
        {
            var input = connection.Transport.Input;
            var read = await input.ReadAsync(connection.ConnectionClosed).ConfigureAwait(false);
            var isTls = !read.Buffer.IsEmpty && read.Buffer.FirstSpan[0] == 0x16;
            input.AdvanceTo(read.Buffer.Start);
            await (isTls ? next(connection) : plainHttp!(connection)).ConfigureAwait(false);
        });
        listen.UseHttps(configureHttps);

        // Middleware is composed last-to-first, so this captures the HTTP handler that sits behind
        // the TLS middleware before the sniffer above ever runs.
        listen.Use(next =>
        {
            plainHttp = next;
            return next;
        });
    }

    private async Task StopListenerAsync()
    {
        if (_app is null)
        {
            return;
        }

        var app = _app;
        _app = null;
        await app.StopAsync().ConfigureAwait(false);
        await app.DisposeAsync().ConfigureAwait(false);
    }

    private async Task HandleAsync(HttpContext http)
    {
        var response = http.Response;
        var origin = http.Request.Headers.Origin.ToString();
        response.Headers["Access-Control-Allow-Origin"] = string.IsNullOrEmpty(origin) ? "*" : origin;
        response.Headers["Access-Control-Allow-Credentials"] = "true";
        response.Headers["Access-Control-Expose-Headers"] = "Location, Date, X-Plex-Protocol";
        response.Headers["X-Plex-Protocol"] = "1.0";

        if (http.Request.Method == "OPTIONS")
        {
            response.Headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, DELETE, OPTIONS, HEAD";
            response.Headers["Access-Control-Allow-Headers"] = http.Request.Headers.AccessControlRequestHeaders.ToString();
            response.Headers["Access-Control-Max-Age"] = "1209600";
            response.StatusCode = 204;
            return;
        }

        var path = http.Request.Path.Value ?? "/";
        var match = _router.Match(http.Request.Method, path);
        if (match is null)
        {
            // Once per method+path at Information: while testing clients, this is the list of what's missing.
            if (_unhandled.TryAdd(http.Request.Method + " " + path, 0))
            {
                _logger.LogInformation("Unhandled Plex request {Method} {Path} from {Client}", http.Request.Method, path, http.Request.Headers["X-Plex-Product"].ToString());
            }

            response.StatusCode = 404;
            return;
        }

        var ctx = new PlexContext(http, match.Values);
        try
        {
            if (!match.Route.Anonymous)
            {
                var auth = await _auth.AuthenticateAsync(ctx).ConfigureAwait(false);
                ctx.Principal = auth.Principal;
                if (ctx.Principal is null)
                {
                    _logger.LogInformation(
                        "401 for {Method} {Path} from {Remote} ({Product}) [token {Fingerprint}]: {Reason}",
                        http.Request.Method,
                        path,
                        http.Request.Headers["X-Forwarded-For"].FirstOrDefault() ?? http.Connection.RemoteIpAddress?.ToString(),
                        ctx.Product,
                        ctx.Token is { Length: > 0 } t ? PlexAuthService.Fingerprint(t) : "none",
                        auth.Reason);
                    response.StatusCode = 401;
                    response.ContentType = "text/html";
                    await response.WriteAsync("<html><head><title>Unauthorized</title></head><body><h1>401 Unauthorized</h1></body></html>").ConfigureAwait(false);
                    return;
                }
            }

            await match.Route.Handler(ctx).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            // Client went away mid-stream; normal for media playback.
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.ConnectionRefused })
        {
            // Jellyfin's own web server isn't up (starting or restarting); the client can retry.
            _logger.LogWarning("Jellyfin isn't accepting connections yet; answered {Method} {Path} with 503", http.Request.Method, path);
            if (!response.HasStarted)
            {
                response.StatusCode = 503;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling Plex request {Method} {Path}", http.Request.Method, path);
            if (!response.HasStarted)
            {
                response.StatusCode = 500;
            }
        }
    }
}
