using Jellyfin.Plugin.ShapeShifter.Server;
using MediaBrowser.Controller.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShapeShifter.Playback;

/// <summary>
/// Talks to Jellyfin's own HTTP API over loopback for things that are only exposed there
/// (image resizing, HLS transcoding). Reusing those endpoints keeps Jellyfin's transcoder,
/// hardware acceleration and throttling in charge rather than reimplementing them.
/// </summary>
public sealed class JellyfinLoopback
{
    private const string ApiKeyName = Plugin.PluginName;

    private static readonly HashSet<string> CopiedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Content-Length", "Content-Range", "Accept-Ranges", "Cache-Control", "Last-Modified", "ETag",
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAuthenticationManager _authenticationManager;
    private readonly ILogger<JellyfinLoopback> _logger;
    private readonly SemaphoreSlim _keyLock = new(1, 1);

    public JellyfinLoopback(IHttpClientFactory httpClientFactory, IAuthenticationManager authenticationManager, ILogger<JellyfinLoopback> logger)
    {
        _httpClientFactory = httpClientFactory;
        _authenticationManager = authenticationManager;
        _logger = logger;
    }

    public static string BaseUrl => Plugin.Instance!.Configuration.JellyfinBaseUrl.TrimEnd('/');

    public async Task<string> GetApiKeyAsync()
    {
        var config = Plugin.Instance!.Configuration;
        if (!string.IsNullOrEmpty(config.JellyfinApiKey))
        {
            return config.JellyfinApiKey;
        }

        await _keyLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (string.IsNullOrEmpty(config.JellyfinApiKey))
            {
                var existing = (await _authenticationManager.GetApiKeys().ConfigureAwait(false)).FirstOrDefault(k => k.AppName == ApiKeyName);
                if (existing is null)
                {
                    await _authenticationManager.CreateApiKey(ApiKeyName).ConfigureAwait(false);
                    existing = (await _authenticationManager.GetApiKeys().ConfigureAwait(false)).First(k => k.AppName == ApiKeyName);
                    _logger.LogInformation("Created Jellyfin API key '{Name}' for loopback requests", ApiKeyName);
                }

                config.JellyfinApiKey = existing.AccessToken;
                Plugin.Instance.SaveConfiguration();
            }

            return config.JellyfinApiKey;
        }
        finally
        {
            _keyLock.Release();
        }
    }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string pathAndQuery, PlexContext? ctx, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, BaseUrl + pathAndQuery);
        request.Headers.Add("Authorization", $"MediaBrowser Token=\"{await GetApiKeyAsync().ConfigureAwait(false)}\"");
        if (ctx is not null && ctx.Http.Request.Headers.Range.Count > 0)
        {
            request.Headers.TryAddWithoutValidation("Range", ctx.Http.Request.Headers.Range.ToString());
        }

        return await _httpClientFactory.CreateClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    /// <summary>Streams a Jellyfin response straight back to the Plex client.</summary>
    public async Task ProxyAsync(PlexContext ctx, string pathAndQuery)
    {
        var ct = ctx.Http.RequestAborted;
        using var upstream = await SendAsync(HttpMethod.Get, pathAndQuery, ctx, ct).ConfigureAwait(false);
        CopyHeaders(ctx.Http.Response, upstream);
        if (ctx.Http.Request.Method == "HEAD")
        {
            return;
        }

        await using var body = await upstream.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await body.CopyToAsync(ctx.Http.Response.Body, ct).ConfigureAwait(false);
    }

    /// <summary>Fetches a text response (playlists) so it can be rewritten before returning.</summary>
    public async Task<(int Status, string? ContentType, string Body)> GetTextAsync(string pathAndQuery, CancellationToken ct)
    {
        using var upstream = await SendAsync(HttpMethod.Get, pathAndQuery, null, ct).ConfigureAwait(false);
        var text = await upstream.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ((int)upstream.StatusCode, upstream.Content.Headers.ContentType?.ToString(), text);
    }

    private static void CopyHeaders(HttpResponse response, HttpResponseMessage upstream)
    {
        response.StatusCode = (int)upstream.StatusCode;
        foreach (var header in upstream.Headers.Concat(upstream.Content.Headers))
        {
            if (CopiedResponseHeaders.Contains(header.Key))
            {
                response.Headers[header.Key] = header.Value.ToArray();
            }
        }
    }
}
