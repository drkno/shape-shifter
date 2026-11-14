using System.Globalization;
using Jellyfin.Plugin.ShapeShifter.Data;
using Jellyfin.Plugin.ShapeShifter.Playback;
using Jellyfin.Plugin.ShapeShifter.Plex;
using Jellyfin.Plugin.ShapeShifter.Server;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ShapeShifter.Api;

/// <summary>Watch state: timeline, scrobble, progress, ratings and the sessions list.</summary>
public sealed class TimelineEndpoints : IPlexEndpoints
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ISessionManager _sessionManager;
    private readonly IdMap _ids;
    private readonly MetadataMapper _mapper;
    private readonly PlaybackReporter _reporter;

    public TimelineEndpoints(ILibraryManager libraryManager, IUserDataManager userDataManager, ISessionManager sessionManager, IdMap ids, MetadataMapper mapper, PlaybackReporter reporter)
    {
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _sessionManager = sessionManager;
        _ids = ids;
        _mapper = mapper;
        _reporter = reporter;
    }

    public void Map(PlexRouter.IRouteBuilder routes)
    {
        routes.Any("/:/timeline", Timeline)
            .Any("/:/scrobble", ctx => SetPlayed(ctx, true))
            .Any("/:/unscrobble", ctx => SetPlayed(ctx, false))
            .Any("/:/progress", Progress)
            .Any("/:/rate", Rate)
            .Get("/status/sessions", Sessions)
            .Get("/status/sessions/history/all", ctx => ctx.WriteEmpty());
    }

    private BaseItem? Item(PlexContext ctx, string? ratingKey)
    {
        // Clients send either ratingKey=123 or key=/library/metadata/123.
        ratingKey = (ratingKey ?? ctx.Query("key"))?.Split('/').LastOrDefault();
        var id = _ids.ResolveGuid(IdMap.Item, ratingKey);
        var item = id is null ? null : _libraryManager.GetItemById(id.Value);
        return item is not null && item.IsVisible(ctx.User) ? item : null;
    }

    private async Task Timeline(PlexContext ctx)
    {
        var item = Item(ctx, ctx.Query("ratingKey"));
        if (item is null)
        {
            await ctx.Status(404).ConfigureAwait(false);
            return;
        }

        var state = ctx.Query("state") ?? "playing";
        var time = ctx.QueryLong("time") ?? 0;
        await _reporter.ReportAsync(ctx, item, state, time, ctx.Param("X-Plex-Session-Identifier") ?? ctx.Query("playQueueItemID")).ConfigureAwait(false);

        await ctx.Write(PlexNode.Container()
            .Set("size", 0)
            .Set("terminationCode", null)
            .Set("terminationText", null)).ConfigureAwait(false);
    }

    private async Task SetPlayed(PlexContext ctx, bool played)
    {
        var item = Item(ctx, ctx.Query("key") ?? ctx.Query("ratingKey"));
        if (item is null)
        {
            await ctx.Status(404).ConfigureAwait(false);
            return;
        }

        // Folder overrides cascade to children (marks a whole season/show).
        if (played)
        {
            item.MarkPlayed(ctx.User, DateTime.UtcNow, true);
        }
        else
        {
            item.MarkUnplayed(ctx.User);
        }

        await ctx.WriteEmpty().ConfigureAwait(false);
    }

    private async Task Progress(PlexContext ctx)
    {
        var item = Item(ctx, ctx.Query("key"));
        var time = ctx.QueryLong("time");
        if (item is null || time is null)
        {
            await ctx.Status(404).ConfigureAwait(false);
            return;
        }

        var data = _userDataManager.GetUserData(ctx.User, item);
        if (data is not null)
        {
            data.PlaybackPositionTicks = time.Value * TimeSpan.TicksPerMillisecond;
            data.LastPlayedDate = DateTime.UtcNow;
            _userDataManager.SaveUserData(ctx.User, item, data, UserDataSaveReason.PlaybackProgress, ctx.Http.RequestAborted);
        }

        await ctx.WriteEmpty().ConfigureAwait(false);
    }

    private async Task Rate(PlexContext ctx)
    {
        var item = Item(ctx, ctx.Query("key"));
        var ratingText = ctx.Query("rating");
        if (item is null || ratingText is null)
        {
            await ctx.Status(404).ConfigureAwait(false);
            return;
        }

        var data = _userDataManager.GetUserData(ctx.User, item);
        if (data is not null)
        {
            // Plex: 0-10 (half stars as 1 step), -1 clears.
            var rating = double.Parse(ratingText, CultureInfo.InvariantCulture);
            data.Rating = rating < 0 ? null : rating;
            data.IsFavorite = rating >= 10;
            _userDataManager.SaveUserData(ctx.User, item, data, UserDataSaveReason.UpdateUserRating, ctx.Http.RequestAborted);
        }

        await ctx.WriteEmpty().ConfigureAwait(false);
    }

    private Task Sessions(PlexContext ctx)
    {
        var node = PlexNode.Container();
        var user = ctx.User;
        var isAdmin = ctx.Principal!.IsOwner;
        foreach (var session in _sessionManager.Sessions.Where(s => s.NowPlayingItem is not null && (isAdmin || s.UserId == user.Id)))
        {
            var item = _libraryManager.GetItemById(session.NowPlayingItem.Id);
            if (item is null)
            {
                continue;
            }

            var meta = _mapper.Map(item, user, new MapOptions());
            meta.Set("sessionKey", session.Id)
                .Set("viewOffset", MetadataMapper.Ms(session.PlayState?.PositionTicks));
            meta.Add(new PlexNode("User").Set("id", session.UserId.ToString("N")).Set("title", session.UserName));
            meta.Add(new PlexNode("Player")
                .Set("address", session.RemoteEndPoint)
                .Set("device", session.DeviceName)
                .Set("machineIdentifier", session.DeviceId)
                .Set("platform", session.Client)
                .Set("product", session.Client)
                .Set("title", session.DeviceName)
                .Set("state", session.PlayState?.IsPaused == true ? "paused" : "playing")
                .Set("local", true));
            meta.Add(new PlexNode("Session").Set("id", session.Id).Set("bandwidth", 0).Set("location", "lan"));
            node.Add(meta);
        }

        return ctx.Write(node.WithSize());
    }
}
