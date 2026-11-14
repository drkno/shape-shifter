using System.Collections.Concurrent;
using System.Security.Cryptography;
using Jellyfin.Plugin.ShapeShifter.Playback.Dash;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShapeShifter.Playback;

/// <summary>
/// A Plex transcode session: HLS backed by Jellyfin's HLS endpoint, or DASH backed by our own
/// ffmpeg job (<see cref="Dash"/>).
/// </summary>
public sealed record TranscodeSession(string PlexSessionId, string Secret, Guid ItemId, string MediaSourceId, string DeviceId, string PlaySessionId)
{
    public DateTime LastAccess { get; set; } = DateTime.UtcNow;

    public DashJob? Dash { get; set; }
}

/// <summary>
/// Tracks transcode sessions. Segment URLs handed to clients carry a random secret instead of the
/// X-Plex-Token (players often drop query strings on segment requests), so the secret is what
/// authorises segment fetches. Idle DASH jobs are stopped and their files removed.
/// </summary>
public sealed class TranscodeSessions : IAsyncDisposable
{
    private static readonly TimeSpan Expiry = TimeSpan.FromHours(6);
    private static readonly TimeSpan DashIdle = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, TranscodeSession> _sessions = new(StringComparer.Ordinal);
    private readonly ILogger<TranscodeSessions> _logger;
    private readonly Timer _reaper;

    public TranscodeSessions(ILogger<TranscodeSessions> logger)
    {
        _logger = logger;
        _reaper = new Timer(_ => _ = ReapAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public TranscodeSession Create(string plexSessionId, Guid itemId, string mediaSourceId, string deviceId)
    {
        var session = new TranscodeSession(
            plexSessionId,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            itemId,
            mediaSourceId,
            deviceId,
            Guid.NewGuid().ToString("N"));
        _sessions[plexSessionId] = session;
        return session;
    }

    public TranscodeSession? Get(string plexSessionId, string secret)
    {
        if (_sessions.TryGetValue(plexSessionId, out var session)
            && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(session.Secret), System.Text.Encoding.ASCII.GetBytes(secret)))
        {
            session.LastAccess = DateTime.UtcNow;
            return session;
        }

        return null;
    }

    /// <summary>Marks a session as alive (Plex clients ping while paused).</summary>
    public void Touch(string plexSessionId)
    {
        if (_sessions.TryGetValue(plexSessionId, out var session))
        {
            session.LastAccess = DateTime.UtcNow;
        }
    }

    public async Task<TranscodeSession?> RemoveAsync(string plexSessionId)
    {
        if (!_sessions.TryRemove(plexSessionId, out var session))
        {
            return null;
        }

        if (session.Dash is { } dash)
        {
            await dash.DisposeAsync().ConfigureAwait(false);
        }

        return session;
    }

    public async ValueTask DisposeAsync()
    {
        await _reaper.DisposeAsync().ConfigureAwait(false);
        foreach (var key in _sessions.Keys)
        {
            await RemoveAsync(key).ConfigureAwait(false);
        }
    }

    private async Task ReapAsync()
    {
        var now = DateTime.UtcNow;
        foreach (var (key, session) in _sessions)
        {
            var dashIdle = session.Dash is { } d && now - Max(d.LastAccess, session.LastAccess) > DashIdle;
            if (dashIdle || now - session.LastAccess > Expiry)
            {
                _logger.LogDebug("Ending idle transcode session {Session}", key);
                try
                {
                    await RemoveAsync(key).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up transcode session {Session}", key);
                }
            }
        }
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
