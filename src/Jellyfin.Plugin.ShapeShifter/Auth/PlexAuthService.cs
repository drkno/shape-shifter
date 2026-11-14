using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Xml.Linq;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.ShapeShifter.Configuration;
using Jellyfin.Plugin.ShapeShifter.Server;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShapeShifter.Auth;

/// <summary>A Plex account resolved from an X-Plex-Token and mapped to a Jellyfin user.</summary>
public sealed record PlexPrincipal(string PlexAccountId, string PlexUsername, bool IsOwner, User User);

/// <summary>The outcome of authenticating a request: a principal, or the reason there isn't one.</summary>
public sealed record AuthResult(PlexPrincipal? Principal, string? Reason);

/// <summary>
/// Resolves X-Plex-Token values the same way PMS does: the server's own token and the owner's
/// account token mean "owner"; shared users present per-server access tokens which plex.tv lists
/// in /api/servers/{machineId}/shared_servers; anything else is looked up on plex.tv.
/// </summary>
public sealed class PlexAuthService
{
    private static readonly TimeSpan PositiveTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SharedRefreshInterval = TimeSpan.FromMinutes(1);

    private readonly IUserManager _userManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PlexAuthService> _logger;
    private readonly ConcurrentDictionary<string, (AuthResult Result, DateTime Expires)> _cache = new();
    private readonly PlexShares _shares;

    public PlexAuthService(IUserManager userManager, IHttpClientFactory httpClientFactory, PlexShares shares, ILogger<PlexAuthService> logger)
    {
        _shares = shares;
        _userManager = userManager;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    public void ClearCache()
    {
        _cache.Clear();
        _shares.ClearCache();
    }

    /// <summary>Resolves the request's token. On failure, <see cref="AuthResult.Reason"/> says why (for the log).</summary>
    public async Task<AuthResult> AuthenticateAsync(PlexContext ctx)
    {
        var token = ctx.Token;
        if (string.IsNullOrEmpty(token))
        {
            return new(null, "no X-Plex-Token in headers or query string");
        }

        if (_cache.TryGetValue(token, out var cached) && cached.Expires > DateTime.UtcNow)
        {
            return cached.Result;
        }

        AuthResult result;
        try
        {
            result = await ResolveAsync(token, ctx.Http.RequestAborted).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            // plex.tv unreachable: keep serving clients we already know rather than locking everyone out.
            _logger.LogWarning(ex, "plex.tv token validation failed");
            return cached.Result?.Principal is not null ? cached.Result : new(null, "plex.tv unreachable: " + ex.Message);
        }

        if (result.Principal is not null && cached.Result?.Principal is null)
        {
            _logger.LogInformation(
                "Plex account {Username} ({Id}{Owner}) signed in as Jellyfin user {User} [token {Fingerprint}]",
                result.Principal.PlexUsername,
                result.Principal.PlexAccountId,
                result.Principal.IsOwner ? ", owner" : string.Empty,
                result.Principal.User.Username,
                Fingerprint(token));
        }

        _cache[token] = (result, DateTime.UtcNow + (result.Principal is null ? NegativeTtl : PositiveTtl));
        return result;
    }

    /// <summary>A short, non-reversible tag for correlating a token across log lines without logging it.</summary>
    public static string Fingerprint(string token) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)))[..8].ToLowerInvariant();

    private async Task<AuthResult> ResolveAsync(string token, CancellationToken ct)
    {
        var config = Config;

        var local = config.UserMappings.FirstOrDefault(m => !string.IsNullOrEmpty(m.LocalToken) && FixedTimeEquals(m.LocalToken, token));
        if (local is not null)
        {
            return Mapped(MapAccount(local.PlexAccountId, local.PlexUsername, IsOwnerId(local.PlexAccountId)), local.PlexUsername);
        }

        if (!string.IsNullOrEmpty(config.ServerToken) && FixedTimeEquals(config.ServerToken, token))
        {
            return Mapped(MapAccount(config.OwnerPlexAccountId, config.OwnerPlexUsername, true), config.OwnerPlexUsername);
        }

        if (!config.EnablePlexTvAuth)
        {
            return new(null, "unknown token and plex.tv validation is turned off");
        }

        var shared = await _shares.GetAsync(false, ct).ConfigureAwait(false);
        var share = shared.FirstOrDefault(x => FixedTimeEquals(x.AccessToken, token));
        if (share is null)
        {
            shared = await _shares.GetAsync(true, ct).ConfigureAwait(false);
            share = shared.FirstOrDefault(x => FixedTimeEquals(x.AccessToken, token));
        }

        if (share is not null)
        {
            // A sharee without a Jellyfin user gets one on first connection (AutoCreateUsers).
            var principal = MapAccount(share.AccountId, share.Username, false);
            if (principal is null && await _shares.EnsureUserAsync(share).ConfigureAwait(false) is not null)
            {
                principal = MapAccount(share.AccountId, share.Username, false);
            }

            return Mapped(principal, share.Username);
        }

        var (account, status) = await GetPlexTvAccountAsync(token, ct).ConfigureAwait(false);
        if (account is null)
        {
            return new(null, $"plex.tv didn't recognise the token (HTTP {status})");
        }

        await EnsureOwnerKnownAsync(ct).ConfigureAwait(false);
        var isOwner = IsOwnerId(account.Value.Id);
        if (!isOwner && !IsKnownAccount(account.Value.Id, shared))
        {
            return new(null, $"plex.tv account {account.Value.Username} ({account.Value.Id}) is neither the owner ({Config.OwnerPlexAccountId}) nor shared with this server");
        }

        return Mapped(MapAccount(account.Value.Id, account.Value.Username, isOwner), account.Value.Username);
    }

    private static AuthResult Mapped(PlexPrincipal? principal, string username) =>
        new(principal, principal is null ? $"Plex account {username} has no Jellyfin user (add a user mapping)" : null);

    /// <summary>Fills in the owner's account id/username from the server token the first time it's needed.</summary>
    private async Task EnsureOwnerKnownAsync(CancellationToken ct)
    {
        var config = Config;
        if (!string.IsNullOrEmpty(config.OwnerPlexAccountId) || string.IsNullOrEmpty(config.ServerToken))
        {
            return;
        }

        var (owner, status) = await GetPlexTvAccountAsync(config.ServerToken, ct).ConfigureAwait(false);
        if (owner is null)
        {
            _logger.LogWarning("Couldn't resolve the server owner from the server token (plex.tv HTTP {Status})", status);
        }
        else
        {
            config.OwnerPlexAccountId = owner.Value.Id;
            if (string.IsNullOrEmpty(config.OwnerPlexUsername))
            {
                config.OwnerPlexUsername = owner.Value.Username;
            }

            Plugin.Instance!.SaveConfiguration();
            _logger.LogInformation("Resolved server owner as plex.tv account {Username} ({Id})", owner.Value.Username, owner.Value.Id);
        }
    }

    private static bool IsOwnerId(string accountId)
    {
        var owner = Config.OwnerPlexAccountId;
        return !string.IsNullOrEmpty(owner) && owner == accountId;
    }

    private static bool IsKnownAccount(string accountId, IReadOnlyList<PlexShare> shared) =>
        shared.Any(v => v.AccountId == accountId) || Config.UserMappings.Any(m => m.PlexAccountId == accountId);

    public PlexPrincipal? MapAccount(string accountId, string username, bool isOwner)
    {
        var config = Config;
        var mapping = config.UserMappings.FirstOrDefault(m => !string.IsNullOrEmpty(m.PlexAccountId) && m.PlexAccountId == accountId)
            ?? config.UserMappings.FirstOrDefault(m => !string.IsNullOrEmpty(m.PlexUsername) && string.Equals(m.PlexUsername, username, StringComparison.OrdinalIgnoreCase));

        User? user = null;
        if (mapping is not null && Guid.TryParse(mapping.JellyfinUserId, out var userId))
        {
            user = _userManager.GetUserById(userId);
        }

        if (user is null && !string.IsNullOrEmpty(username))
        {
            user = _userManager.GetUserByName(username);
        }

        if (user is null && isOwner)
        {
            user = _userManager.GetUsers().FirstOrDefault(u => u.HasPermission(PermissionKind.IsAdministrator));
        }

        if (user is null)
        {
            _logger.LogWarning("Plex account {Username} ({Id}) has no Jellyfin user mapping", username, accountId);
            return null;
        }

        return new PlexPrincipal(accountId, string.IsNullOrEmpty(username) ? user.Username : username, isOwner, user);
    }

    private async Task<((string Id, string Username)? Account, int Status)> GetPlexTvAccountAsync(string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://plex.tv/api/v2/user");
        AddPlexHeaders(request, token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _httpClientFactory.CreateClient().SendAsync(request, ct).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
        {
            return (null, status);
        }

        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
        var root = json.RootElement;
        var id = root.TryGetProperty("id", out var idEl) ? idEl.ToString() : null;
        var username = root.TryGetProperty("username", out var u) ? u.GetString() : null;
        if (string.IsNullOrEmpty(id))
        {
            return (null, status);
        }

        return ((id, username ?? string.Empty), status);
    }

    internal static void AddPlexHeaders(HttpRequestMessage request, string token)
    {
        var config = Config;
        request.Headers.Add("X-Plex-Token", token);
        request.Headers.Add("X-Plex-Client-Identifier", config.MachineIdentifier);
        request.Headers.Add("X-Plex-Product", "Plex Media Server");
        request.Headers.Add("X-Plex-Version", config.AdvertisedVersion);
    }

    private static bool FixedTimeEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));
}
