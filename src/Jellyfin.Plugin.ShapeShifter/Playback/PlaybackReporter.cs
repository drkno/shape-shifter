using System.Collections.Concurrent;
using Jellyfin.Plugin.ShapeShifter.Server;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShapeShifter.Playback;

/// <summary>
/// Turns Plex /:/timeline reports into Jellyfin playback sessions, so resume points, played
/// status, the Jellyfin dashboard and "now playing" all behave as if a Jellyfin client played it.
/// </summary>
public sealed class PlaybackReporter
{
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<PlaybackReporter> _logger;
    private readonly ConcurrentDictionary<string, ActivePlayback> _active = new();

    public PlaybackReporter(ISessionManager sessionManager, ILogger<PlaybackReporter> logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }

    public async Task<SessionInfo> GetSessionAsync(PlexContext ctx)
    {
        return await _sessionManager.LogSessionActivity(
            ctx.Product,
            ctx.ProductVersion,
            "plex-" + ctx.ClientIdentifier,
            ctx.DeviceName,
            ctx.Http.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            ctx.User).ConfigureAwait(false);
    }

    /// <summary>Handles one timeline update. state is playing|paused|buffering|stopped.</summary>
    public async Task ReportAsync(PlexContext ctx, BaseItem item, string state, long positionMs, string? playSessionId)
    {
        var session = await GetSessionAsync(ctx).ConfigureAwait(false);
        var key = session.Id;
        var ticks = positionMs * TimeSpan.TicksPerMillisecond;
        var paused = state == "paused";

        if (_active.TryGetValue(key, out var current) && current.ItemId != item.Id)
        {
            await StopAsync(session, current, current.LastTicks).ConfigureAwait(false);
            _active.TryRemove(key, out _);
            current = null;
        }

        if (state == "stopped")
        {
            if (current is not null)
            {
                _active.TryRemove(key, out _);
            }

            await StopAsync(session, current ?? new ActivePlayback(item.Id, playSessionId ?? Guid.NewGuid().ToString("N")), ticks).ConfigureAwait(false);
            return;
        }

        if (current is null)
        {
            current = new ActivePlayback(item.Id, playSessionId ?? Guid.NewGuid().ToString("N")) { LastTicks = ticks };
            _active[key] = current;
            await _sessionManager.OnPlaybackStart(new PlaybackStartInfo
            {
                ItemId = item.Id,
                SessionId = session.Id,
                PlaySessionId = current.PlaySessionId,
                PositionTicks = ticks,
                IsPaused = paused,
                CanSeek = true,
                PlayMethod = PlayMethod.DirectPlay,
            }).ConfigureAwait(false);
            return;
        }

        current.LastTicks = ticks;
        await _sessionManager.OnPlaybackProgress(new PlaybackProgressInfo
        {
            ItemId = item.Id,
            SessionId = session.Id,
            PlaySessionId = current.PlaySessionId,
            PositionTicks = ticks,
            IsPaused = paused,
            CanSeek = true,
            PlayMethod = PlayMethod.DirectPlay,
        }).ConfigureAwait(false);
    }

    private async Task StopAsync(SessionInfo session, ActivePlayback playback, long ticks)
    {
        try
        {
            await _sessionManager.OnPlaybackStopped(new PlaybackStopInfo
            {
                ItemId = playback.ItemId,
                SessionId = session.Id,
                PlaySessionId = playback.PlaySessionId,
                PositionTicks = ticks,
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to report playback stop for {Item}", playback.ItemId);
        }
    }

    private sealed class ActivePlayback(Guid itemId, string playSessionId)
    {
        public Guid ItemId { get; } = itemId;

        public string PlaySessionId { get; } = playSessionId;

        public long LastTicks { get; set; }
    }
}
