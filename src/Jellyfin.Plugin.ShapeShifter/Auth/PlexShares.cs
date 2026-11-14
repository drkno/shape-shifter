using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.ShapeShifter.Configuration;
using Jellyfin.Plugin.ShapeShifter.Data;
using MediaBrowser.Controller.Library;
using Jellyfin.Data.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShapeShifter.Auth;

/// <summary>A plex.tv share of this server: who it's with, their per-server token and which libraries.</summary>
/// <param name="SectionKeys">Plex section keys shared with the user (ignored when <paramref name="AllLibraries"/>).</param>
public sealed record PlexShare(string AccountId, string Username, string AccessToken, bool AllLibraries, IReadOnlyList<long> SectionKeys);

/// <summary>
/// The server's plex.tv shares (Manage Library Access), and the Jellyfin users behind them. With
/// AutoCreateUsers on, a share whose Plex user has no Jellyfin user gets one, named after their
/// Plex username: the same name PlexSSO's OIDC sends as preferred_username, so the SSO plugin
/// links a later web sign-in to this account rather than creating another. Users created here are
/// "managed": their library access follows the share, a revoked share disables them (keeping
/// their history), and changes made elsewhere (the SSO plugin re-applies its own folder settings
/// at every sign-in) are put back. Jellyfin users that already existed are linked, not managed.
/// </summary>
public sealed partial class PlexShares
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);
    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromMinutes(1);

    private readonly IUserManager _userManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IdMap _ids;
    private readonly ILogger<PlexShares> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IReadOnlyList<PlexShare> _shares = [];
    private DateTime _fetched = DateTime.MinValue;
    private bool _enforcing;

    public PlexShares(IUserManager userManager, IHttpClientFactory httpClientFactory, IdMap ids, ILogger<PlexShares> logger)
    {
        _userManager = userManager;
        _httpClientFactory = httpClientFactory;
        _ids = ids;
        _logger = logger;
        _userManager.OnUserUpdated += OnUserUpdated;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    public void ClearCache() => _fetched = DateTime.MinValue;

    /// <summary>The current shares; <paramref name="refresh"/> refetches unless that happened within the last minute.</summary>
    public async Task<IReadOnlyList<PlexShare>> GetAsync(bool refresh, CancellationToken ct)
    {
        var config = Config;
        var age = DateTime.UtcNow - _fetched;
        if (string.IsNullOrEmpty(config.ServerToken) || age < MinRefreshInterval || (!refresh && age < CacheFor))
        {
            return _shares;
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://plex.tv/api/servers/{config.MachineIdentifier}/shared_servers");
            PlexAuthService.AddPlexHeaders(request, config.ServerToken);
            using var response = await _httpClientFactory.CreateClient().SendAsync(request, ct).ConfigureAwait(false);
            _fetched = DateTime.UtcNow;
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("plex.tv shared_servers returned {Status}", response.StatusCode);
                return _shares;
            }

            _shares = Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            _logger.LogDebug("Loaded {Count} shares from plex.tv", _shares.Count);
            return _shares;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Parses plex.tv's /api/servers/{id}/shared_servers XML.</summary>
    internal static List<PlexShare> Parse(string xml)
    {
        var shares = new List<PlexShare>();
        foreach (var el in XDocument.Parse(xml).Descendants("SharedServer"))
        {
            var token = (string?)el.Attribute("accessToken");
            var accountId = (string?)el.Attribute("userID");
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(accountId))
            {
                continue;
            }

            var sections = el.Elements("Section")
                .Where(s => (string?)s.Attribute("shared") is "1" or "true")
                .Select(s => long.TryParse((string?)s.Attribute("key"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var key) ? key : (long?)null)
                .OfType<long>()
                .ToList();
            shares.Add(new PlexShare(accountId, (string?)el.Attribute("username") ?? string.Empty, token, (string?)el.Attribute("allLibraries") is "1" or "true", sections));
        }

        return shares;
    }

    /// <summary>Creates, links, updates and disables Jellyfin users to match the current shares.</summary>
    public async Task SyncAsync(CancellationToken ct)
    {
        if (!Config.AutoCreateUsers || string.IsNullOrEmpty(Config.ServerToken))
        {
            return;
        }

        var shares = await GetAsync(true, ct).ConfigureAwait(false);
        foreach (var share in shares)
        {
            await EnsureUserAsync(share).ConfigureAwait(false);
        }

        // Managed users whose share is gone lose access, but keep their account and history.
        var current = shares.Select(s => s.AccountId).ToHashSet(StringComparer.Ordinal);
        foreach (var accountId in Config.ManagedPlexAccountIds.Where(id => !current.Contains(id)))
        {
            if (UserFor(accountId) is { } user && !user.HasPermission(PermissionKind.IsDisabled))
            {
                user.SetPermission(PermissionKind.IsDisabled, true);
                await UpdateAsync(user).ConfigureAwait(false);
                _logger.LogInformation("Disabled Jellyfin user {User}: their plex.tv share was removed", user.Username);
            }
        }
    }

    /// <summary>The Jellyfin user for a share, creating it first when AutoCreateUsers is on.</summary>
    public async Task<User?> EnsureUserAsync(PlexShare share)
    {
        var config = Config;
        var mapping = config.UserMappings.FirstOrDefault(m => m.PlexAccountId == share.AccountId);
        var user = UserFor(share.AccountId) ?? (string.IsNullOrEmpty(share.Username) ? null : _userManager.GetUserByName(share.Username));

        if (user is null)
        {
            if (!config.AutoCreateUsers)
            {
                return null;
            }

            user = await _userManager.CreateUserAsync(JellyfinName(share)).ConfigureAwait(false);

            // No usable password: sign-in is through Plex or PlexSSO. (A blank password would let anyone in.)
            await _userManager.ChangePassword(user.Id, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))).ConfigureAwait(false);

            // Re-read: saving the object from before the password change would put the blank password back.
            user = _userManager.GetUserById(user.Id) ?? throw new InvalidOperationException("Created user disappeared");
            config.ManagedPlexAccountIds = [.. config.ManagedPlexAccountIds.Where(id => id != share.AccountId), share.AccountId];
            _logger.LogInformation("Created Jellyfin user {User} for Plex user {Plex} ({Id}), who this server is shared with", user.Username, share.Username, share.AccountId);
        }

        if (mapping is null)
        {
            config.UserMappings = [.. config.UserMappings, new UserMapping { PlexAccountId = share.AccountId, PlexUsername = share.Username, JellyfinUserId = user.Id.ToString("N") }];
            Plugin.Instance!.SaveConfiguration();
        }
        else if (mapping.JellyfinUserId != user.Id.ToString("N"))
        {
            mapping.JellyfinUserId = user.Id.ToString("N");
            Plugin.Instance!.SaveConfiguration();
        }

        if (config.ManagedPlexAccountIds.Contains(share.AccountId) && ApplyAccess(user, share))
        {
            await UpdateAsync(user).ConfigureAwait(false);
            _logger.LogInformation(
                "Set {User}'s library access to match their plex.tv share ({Access})",
                user.Username,
                share.AllLibraries ? "all libraries" : $"{share.SectionKeys.Count} libraries");
        }

        return user;
    }

    /// <summary>Makes a managed user's access match the share. Returns whether anything changed.</summary>
    private bool ApplyAccess(User user, PlexShare share)
    {
        var folders = share.SectionKeys
            .Select(k => _ids.ResolveGuid(IdMap.Section, k.ToString(CultureInfo.InvariantCulture)))
            .OfType<Guid>()
            .Select(g => g.ToString("N"))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var changed = false;
        if (user.HasPermission(PermissionKind.EnableAllFolders) != share.AllLibraries)
        {
            user.SetPermission(PermissionKind.EnableAllFolders, share.AllLibraries);
            changed = true;
        }

        if (!share.AllLibraries)
        {
            var current = user.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders).Select(g => g.ToString("N")).Order(StringComparer.Ordinal).ToArray();
            if (!current.SequenceEqual(folders))
            {
                user.SetPreference(PreferenceKind.EnabledFolders, folders);
                changed = true;
            }
        }

        foreach (var (permission, value) in new[] { (PermissionKind.IsDisabled, false), (PermissionKind.IsAdministrator, false) })
        {
            if (user.HasPermission(permission) != value)
            {
                user.SetPermission(permission, value);
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>Puts a managed user's access back when something else (the SSO plugin at sign-in) changes it.</summary>
    private void OnUserUpdated(object? sender, GenericEventArgs<User> e)
    {
        var config = Plugin.Instance?.Configuration;
        if (_enforcing || config is null || !config.AutoCreateUsers)
        {
            return;
        }

        var mapping = config.UserMappings.FirstOrDefault(m => m.JellyfinUserId == e.Argument.Id.ToString("N"));
        if (mapping is null || !config.ManagedPlexAccountIds.Contains(mapping.PlexAccountId))
        {
            return;
        }

        var share = _shares.FirstOrDefault(s => s.AccountId == mapping.PlexAccountId);
        if (share is not null && _userManager.GetUserById(e.Argument.Id) is { } user && ApplyAccess(user, share))
        {
            _logger.LogInformation("Restored {User}'s library access to match their plex.tv share", user.Username);
            _ = UpdateAsync(user);
        }
    }

    private async Task UpdateAsync(User user)
    {
        _enforcing = true;
        try
        {
            await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
        }
        finally
        {
            _enforcing = false;
        }
    }

    private User? UserFor(string accountId) =>
        Config.UserMappings.FirstOrDefault(m => m.PlexAccountId == accountId) is { } m && Guid.TryParse(m.JellyfinUserId, out var id)
            ? _userManager.GetUserById(id)
            : null;

    /// <summary>The Plex username, if Jellyfin accepts it as a user name; otherwise a stable fallback.</summary>
    internal static string JellyfinName(PlexShare share) =>
        !string.IsNullOrWhiteSpace(share.Username) && ValidJellyfinName().IsMatch(share.Username) ? share.Username : "plex-" + share.AccountId;

    // Jellyfin's own user name rule.
    [GeneratedRegex(@"^(?!\s)[\w\ \-'._@+]+(?<!\s)$")]
    private static partial Regex ValidJellyfinName();
}

/// <summary>Keeps Jellyfin users in step with plex.tv shares (see <see cref="PlexShares"/>).</summary>
public sealed class PlexShareSync : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    private readonly PlexShares _shares;
    private readonly ILogger<PlexShareSync> _logger;

    public PlexShareSync(PlexShares shares, ILogger<PlexShareSync> logger)
    {
        _shares = shares;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _shares.SyncAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or ArgumentException)
            {
                _logger.LogWarning(ex, "Syncing Jellyfin users with plex.tv shares failed");
            }

            await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
        }
    }
}
