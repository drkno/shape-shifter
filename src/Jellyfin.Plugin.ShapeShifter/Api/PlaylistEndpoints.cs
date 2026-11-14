using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ShapeShifter.Data;
using Jellyfin.Plugin.ShapeShifter.Plex;
using Jellyfin.Plugin.ShapeShifter.Server;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShapeShifter.Api;

/// <summary>
/// PMS playlists over Jellyfin playlists. Each entry gets a stable playlistItemID (mapped to
/// Jellyfin's entry id) so clients can remove and reorder entries.
/// </summary>
public sealed class PlaylistEndpoints : IPlexEndpoints
{
    private readonly ILibraryManager _libraryManager;
    private readonly IPlaylistManager _playlistManager;
    private readonly IdMap _ids;
    private readonly MetadataMapper _mapper;
    private readonly ILogger<PlaylistEndpoints> _logger;

    public PlaylistEndpoints(ILibraryManager libraryManager, IPlaylistManager playlistManager, IdMap ids, MetadataMapper mapper, ILogger<PlaylistEndpoints> logger)
    {
        _libraryManager = libraryManager;
        _playlistManager = playlistManager;
        _ids = ids;
        _mapper = mapper;
        _logger = logger;
    }

    public void Map(PlexRouter.IRouteBuilder routes)
    {
        routes.Get("/playlists", List)
            .Get("/playlists/all", List)
            .Post("/playlists", Create)
            .Get("/playlists/{id}", Get)
            .Put("/playlists/{id}", Update)
            .Delete("/playlists/{id}", Delete)
            .Get("/playlists/{id}/items", Items)
            .Put("/playlists/{id}/items", AddItems)
            .Delete("/playlists/{id}/items", Clear)
            .Delete("/playlists/{id}/items/{item}", RemoveItem)
            .Put("/playlists/{id}/items/{item}/move", Move);
    }

    private Playlist? Find(PlexContext ctx, string key)
    {
        var id = _ids.ResolveGuid(IdMap.Item, key);
        return id is not null && _libraryManager.GetItemById(id.Value) is Playlist playlist && playlist.IsVisible(ctx.User) ? playlist : null;
    }

    private static string PlaylistType(Playlist p) => p.MediaType switch { MediaType.Audio => "audio", MediaType.Photo => "photo", _ => "video" };

    private Task List(PlexContext ctx)
    {
        var wanted = ctx.Query("playlistType");
        var playlists = _playlistManager.GetPlaylists(ctx.User.Id)
            .Where(p => string.IsNullOrEmpty(wanted) || PlaylistType(p) == wanted)
            .OrderBy(p => p.SortName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ctx.Query("smart") == "1")
        {
            playlists.Clear();
        }

        var (start, size) = ctx.Paging();
        var page = playlists.Skip(start).Take(size ?? int.MaxValue).ToList();
        var node = PlexNode.Container().Set("allowSync", false);
        node.AddRange(_mapper.Map(page.Cast<BaseItem>().ToList(), ctx.User, new MapOptions { IncludeMedia = false }));
        return ctx.Write(node.WithSize(playlists.Count, start));
    }

    private Task Get(PlexContext ctx)
    {
        var playlist = Find(ctx, ctx.Route("id"));
        return playlist is null ? ctx.Status(404) : WritePlaylist(ctx, playlist);
    }

    private Task WritePlaylist(PlexContext ctx, Playlist playlist) =>
        ctx.Write(PlexNode.Container().Set("allowSync", false).Add(_mapper.Map(playlist, ctx.User, new MapOptions { Detailed = true, IncludeMedia = false })).WithSize());

    private async Task Create(PlexContext ctx)
    {
        var title = ctx.Query("title");
        if (string.IsNullOrWhiteSpace(title))
        {
            await ctx.Status(400).ConfigureAwait(false);
            return;
        }

        if (ctx.QueryBool("smart"))
        {
            // Smart playlists are filter queries; Jellyfin has no equivalent.
            await ctx.Status(400).ConfigureAwait(false);
            return;
        }

        var items = PlexUris.Items(ctx.Query("uri"), ctx.User, _ids, _libraryManager);
        var mediaType = ctx.Query("type") switch { "audio" => MediaType.Audio, "photo" => MediaType.Photo, _ => MediaType.Video };
        var result = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
        {
            Name = title,
            ItemIdList = items.Select(i => i.Id).ToArray(),
            MediaType = mediaType,
            UserId = ctx.User.Id,
        }).ConfigureAwait(false);

        if (!Guid.TryParse(result.Id, out var id) || _libraryManager.GetItemById(id) is not Playlist playlist)
        {
            await ctx.Status(500).ConfigureAwait(false);
            return;
        }

        _logger.LogInformation("Created playlist '{Title}' with {Count} items for {User}", title, items.Count, ctx.User.Username);
        await WritePlaylist(ctx, playlist).ConfigureAwait(false);
    }

    private async Task Update(PlexContext ctx)
    {
        var playlist = Find(ctx, ctx.Route("id"));
        if (playlist is null)
        {
            await ctx.Status(404).ConfigureAwait(false);
            return;
        }

        var changed = false;
        if (ctx.Query("title") is { Length: > 0 } title)
        {
            playlist.Name = title;
            changed = true;
        }

        if (ctx.Query("summary") is { } summary)
        {
            playlist.Overview = summary;
            changed = true;
        }

        if (changed)
        {
            await playlist.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ctx.Http.RequestAborted).ConfigureAwait(false);
        }

        await ctx.WriteEmpty().ConfigureAwait(false);
    }

    private async Task Delete(PlexContext ctx)
    {
        var playlist = Find(ctx, ctx.Route("id"));
        if (playlist is null)
        {
            await ctx.Status(404).ConfigureAwait(false);
            return;
        }

        _libraryManager.DeleteItem(playlist, new DeleteOptions { DeleteFileLocation = true }, true);
        await ctx.WriteEmpty().ConfigureAwait(false);
    }

    /// <summary>Entries in order, each with its Jellyfin entry id and the item it points at.</summary>
    private List<(string EntryId, BaseItem Item)> Entries(Playlist playlist, Jellyfin.Database.Implementations.Entities.User user) =>
        playlist.GetLinkedChildrenInfos()
            .Where(e => e.Item2 is not null && e.Item2.IsVisible(user) && e.Item1.ItemId is not null)
            .Select(e => (e.Item1.ItemId!.Value.ToString("N", CultureInfo.InvariantCulture), e.Item2))
            .ToList();

    private long PlaylistItemId(Playlist playlist, string entryId) => _ids.GetOrAdd(IdMap.PlaylistItem, $"{playlist.Id:N}|{entryId}");

    private string? EntryId(Playlist playlist, string playlistItemId) =>
        long.TryParse(playlistItemId, out var id) && _ids.Resolve(IdMap.PlaylistItem, id) is { } mapped
            && mapped.Split('|') is [var owner, var entry] && owner == playlist.Id.ToString("N") ? entry : null;

    private Task Items(PlexContext ctx)
    {
        var playlist = Find(ctx, ctx.Route("id"));
        if (playlist is null)
        {
            return ctx.Status(404);
        }

        var entries = Entries(playlist, ctx.User);
        var (start, size) = ctx.Paging();
        var page = entries.Skip(start).Take(size ?? int.MaxValue).ToList();
        var mapped = _mapper.Map(page.Select(e => e.Item).ToList(), ctx.User, new MapOptions());
        for (var i = 0; i < page.Count; i++)
        {
            mapped[i].Set("playlistItemID", PlaylistItemId(playlist, page[i].EntryId));
        }

        var rk = _mapper.RatingKey(playlist);
        var node = PlexNode.Container()
            .Set("allowSync", false)
            .Set("ratingKey", rk)
            .Set("key", $"/playlists/{rk}/items")
            .Set("title", playlist.Name)
            .Set("playlistType", PlaylistType(playlist))
            .Set("smart", false)
            .Set("leafCount", entries.Count)
            .Set("duration", MetadataMapper.Ms(entries.Sum(e => e.Item.RunTimeTicks ?? 0)));
        node.AddRange(mapped);
        return ctx.Write(node.WithSize(entries.Count, start));
    }

    private async Task AddItems(PlexContext ctx)
    {
        var playlist = Find(ctx, ctx.Route("id"));
        if (playlist is null)
        {
            await ctx.Status(404).ConfigureAwait(false);
            return;
        }

        var items = PlexUris.Items(ctx.Query("uri"), ctx.User, _ids, _libraryManager);
        if (items.Count > 0)
        {
            await _playlistManager.AddItemToPlaylistAsync(playlist.Id, items.Select(i => i.Id).ToArray(), null, ctx.User.Id).ConfigureAwait(false);
        }

        await WritePlaylist(ctx, playlist).ConfigureAwait(false);
    }

    private async Task RemoveItem(PlexContext ctx)
    {
        var playlist = Find(ctx, ctx.Route("id"));
        var entry = playlist is null ? null : EntryId(playlist, ctx.Route("item"));
        if (playlist is null || entry is null)
        {
            await ctx.Status(404).ConfigureAwait(false);
            return;
        }

        await _playlistManager.RemoveItemFromPlaylistAsync(playlist.Id.ToString("N"), [entry]).ConfigureAwait(false);
        await WritePlaylist(ctx, playlist).ConfigureAwait(false);
    }

    private async Task Clear(PlexContext ctx)
    {
        var playlist = Find(ctx, ctx.Route("id"));
        if (playlist is null)
        {
            await ctx.Status(404).ConfigureAwait(false);
            return;
        }

        var entries = Entries(playlist, ctx.User).Select(e => e.EntryId).ToList();
        if (entries.Count > 0)
        {
            await _playlistManager.RemoveItemFromPlaylistAsync(playlist.Id.ToString("N"), entries).ConfigureAwait(false);
        }

        await WritePlaylist(ctx, playlist).ConfigureAwait(false);
    }

    /// <summary>PMS moves an entry to just after ?after= (or to the top without it).</summary>
    private async Task Move(PlexContext ctx)
    {
        var playlist = Find(ctx, ctx.Route("id"));
        var entry = playlist is null ? null : EntryId(playlist, ctx.Route("item"));
        if (playlist is null || entry is null)
        {
            await ctx.Status(404).ConfigureAwait(false);
            return;
        }

        var order = Entries(playlist, ctx.User).Select(e => e.EntryId).Where(e => e != entry).ToList();
        var newIndex = 0;
        if (ctx.Query("after") is { Length: > 0 } after && after != "0")
        {
            var afterEntry = EntryId(playlist, after);
            var index = afterEntry is null ? -1 : order.IndexOf(afterEntry);
            if (index < 0)
            {
                await ctx.Status(400).ConfigureAwait(false);
                return;
            }

            newIndex = index + 1;
        }

        await _playlistManager.MoveItemAsync(playlist.Id.ToString("N"), entry, newIndex, ctx.User.Id).ConfigureAwait(false);
        await Items(ctx).ConfigureAwait(false);
    }
}
