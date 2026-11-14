using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.ShapeShifter.Configuration;
using Jellyfin.Plugin.ShapeShifter.Data;
using Jellyfin.Plugin.ShapeShifter.Playback;
using Jellyfin.Plugin.ShapeShifter.Plex;
using Jellyfin.Plugin.ShapeShifter.Server;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using Jellyfin.Plugin.ShapeShifter.Library;

namespace Jellyfin.Plugin.ShapeShifter.Api;

/// <summary>/playQueues: create, fetch, add to and clear play queues.</summary>
public sealed partial class PlayQueueEndpoints : IPlexEndpoints
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IdMap _ids;
    private readonly MetadataMapper _mapper;
    private readonly PlayQueueStore _store;

    private readonly Audiobooks _audiobooks;

    public PlayQueueEndpoints(ILibraryManager libraryManager, IUserDataManager userDataManager, IdMap ids, MetadataMapper mapper, PlayQueueStore store, Audiobooks audiobooks)
    {
        _audiobooks = audiobooks;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _ids = ids;
        _mapper = mapper;
        _store = store;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    public void Map(PlexRouter.IRouteBuilder routes)
    {
        routes.Post("/playQueues", Create)
            .Get("/playQueues/{id}", Get)
            .Put("/playQueues/{id}", Add)
            .Put("/playQueues/{id}/shuffle", Get)
            .Put("/playQueues/{id}/unshuffle", Get)
            .Delete("/playQueues/{id}/items", Clear)
            .Delete("/playQueues/{id}/items/{item}", RemoveItem);
    }

    private BaseItem? FromUri(PlexContext ctx, string? uri)
    {
        // server://{machineId}/com.plexapp.plugins.library/library/metadata/{rk}
        // library://{uuid}/item/%2Flibrary%2Fmetadata%2F{rk}
        if (uri is null)
        {
            return null;
        }

        var m = MetadataKey().Match(Uri.UnescapeDataString(uri));
        if (!m.Success)
        {
            return null;
        }

        var id = _ids.ResolveGuid(IdMap.Item, m.Groups["rk"].Value);
        var item = id is null ? null : _libraryManager.GetItemById(id.Value);
        return item is not null && item.IsVisible(ctx.User) ? item : null;
    }

    /// <summary>The chapters of an audiobook author or book key (see Audiobooks), or null for anything else.</summary>
    private IReadOnlyList<BaseItem>? DerivedBookItems(string? uri)
    {
        if (uri is null || MetadataKey().Match(Uri.UnescapeDataString(uri)) is not { Success: true } m
            || _audiobooks.Resolve(m.Groups["rk"].Value) is not { } derived)
        {
            return null;
        }

        return derived.Book?.Chapters ?? derived.Author!.Chapters.ToList();
    }

    private IReadOnlyList<BaseItem> Expand(User user, BaseItem item, bool continuous)
    {
        var dto = new DtoOptions(true);
        switch (item)
        {
            case Series series:
                return series.GetEpisodes(user, dto, false).ToList();
            case Season season:
                return season.GetEpisodes(user, dto, false);
            case MusicAlbum album:
                return album.Tracks.Where(t => t.IsVisible(user)).OrderBy(t => t.ParentIndexNumber ?? 1).ThenBy(t => t.IndexNumber ?? 0).ToList();
            case MusicArtist artist:
                return _libraryManager.GetItemList(new InternalItemsQuery(user)
                {
                    IncludeItemTypes = [BaseItemKind.Audio],
                    ArtistIds = [artist.Id],
                    Recursive = true,
                    OrderBy = [(ItemSortBy.Album, SortOrder.Ascending), (ItemSortBy.ParentIndexNumber, SortOrder.Ascending), (ItemSortBy.IndexNumber, SortOrder.Ascending)],
                    DtoOptions = dto,
                });
            case AudioBook chapter when _audiobooks.Locate(chapter) is { } placement:
                // Playing a chapter plays on through the rest of its book.
                return placement.Book.Chapters.Skip(placement.Index - 1).Where(c => c.IsVisible(user)).ToList();
            case Playlist playlist:
                return playlist.GetManageableItems().Select(i => i.Item2).Where(i => i.IsVisible(user)).ToList();
            case Episode episode when continuous && episode.Series is { } s:
                var all = s.GetEpisodes(user, dto, false).ToList();
                var index = all.FindIndex(e => e.Id == episode.Id);
                return index < 0 ? [episode] : all.Skip(index).ToList();
            default:
                return [item];
        }
    }

    private async Task Create(PlexContext ctx)
    {
        var uri = ctx.Query("uri") ?? ctx.Query("key");
        var derived = DerivedBookItems(uri);
        var item = derived?.FirstOrDefault() ?? FromUri(ctx, uri) ?? (ctx.Query("playlistID") is { } pl ? FromUri(ctx, $"/library/metadata/{pl}") : null);
        if (item is null)
        {
            await ctx.Status(400).ConfigureAwait(false);
            return;
        }

        var items = derived?.ToList() ?? Expand(ctx.User, item, ctx.QueryBool("continuous")).ToList();
        var shuffle = ctx.QueryBool("shuffle");
        if (shuffle)
        {
            Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(items));
        }

        // Selected item: explicit key, else first unwatched leaf (what PMS does for shows).
        var selected = 0;
        if (MetadataKey().Match(Uri.UnescapeDataString(ctx.Query("key") ?? string.Empty)) is { Success: true } km
            && _ids.ResolveGuid(IdMap.Item, km.Groups["rk"].Value) is { } keyId)
        {
            selected = Math.Max(0, items.FindIndex(i => i.Id == keyId));
        }
        else if (item is Series or Season && !shuffle)
        {
            var userData = _userDataManager.GetUserDataBatch(items, ctx.User);
            var firstUnplayed = items.FindIndex(i => !userData.TryGetValue(i.Id, out var d) || !d.Played);
            selected = Math.Max(0, firstUnplayed);
        }

        var queue = _store.Create(ctx.User.Id, uri ?? string.Empty, items.Select(i => i.Id), selected, shuffle);
        await WriteQueue(ctx, queue).ConfigureAwait(false);
    }

    private PlayQueue? Find(PlexContext ctx) =>
        long.TryParse(ctx.Route("id"), out var id) ? _store.Get(id, ctx.User.Id) : null;

    private async Task Get(PlexContext ctx)
    {
        var queue = Find(ctx);
        if (queue is null)
        {
            await ctx.Status(404).ConfigureAwait(false);
            return;
        }

        await WriteQueue(ctx, queue).ConfigureAwait(false);
    }

    private async Task Add(PlexContext ctx)
    {
        var queue = Find(ctx);
        if (queue is null)
        {
            await ctx.Status(404).ConfigureAwait(false);
            return;
        }

        var derivedItems = DerivedBookItems(ctx.Query("uri"));
        if ((derivedItems?.FirstOrDefault() ?? FromUri(ctx, ctx.Query("uri"))) is { } item)
        {
            var added = (derivedItems ?? Expand(ctx.User, item, false)).Select(i => (_store.NextItemId(), i.Id)).ToList();
            if (ctx.QueryBool("next"))
            {
                queue.Items.InsertRange(Math.Min(queue.SelectedIndex + 1, queue.Items.Count), added);
            }
            else
            {
                queue.Items.AddRange(added);
            }

            queue.Version++;
        }

        await WriteQueue(ctx, queue).ConfigureAwait(false);
    }

    private async Task Clear(PlexContext ctx)
    {
        var queue = Find(ctx);
        if (queue is null)
        {
            await ctx.Status(404).ConfigureAwait(false);
            return;
        }

        queue.Items.Clear();
        queue.SelectedIndex = 0;
        queue.Version++;
        await WriteQueue(ctx, queue).ConfigureAwait(false);
    }

    private async Task RemoveItem(PlexContext ctx)
    {
        var queue = Find(ctx);
        if (queue is null || !long.TryParse(ctx.Route("item"), out var itemId))
        {
            await ctx.Status(404).ConfigureAwait(false);
            return;
        }

        var index = queue.Items.FindIndex(i => i.ItemId == itemId);
        if (index >= 0)
        {
            queue.Items.RemoveAt(index);
            if (index < queue.SelectedIndex)
            {
                queue.SelectedIndex--;
            }

            queue.Version++;
        }

        await WriteQueue(ctx, queue).ConfigureAwait(false);
    }

    private Task WriteQueue(PlexContext ctx, PlayQueue queue)
    {
        // Return a window around the selected item, like PMS (window=50 by default).
        var window = ctx.QueryInt("window") ?? 50;
        var start = Math.Max(0, queue.SelectedIndex - (window / 2));
        var slice = queue.Items.Skip(start).Take(window).ToList();
        var items = slice.Select(i => _libraryManager.GetItemById(i.JellyfinId)).ToList();

        var present = slice.Zip(items).Where(p => p.Second is not null).ToList();
        var nodes = _mapper.Map(present.Select(p => p.Second!).ToList(), ctx.User, new MapOptions());

        var node = PlexNode.Container()
            .Set("identifier", MetadataMapper.LibraryIdentifier)
            .Set("mediaTagPrefix", "/system/bundle/media/flags/")
            .Set("mediaTagVersion", 1)
            .Set("playQueueID", queue.Id)
            .Set("playQueueSelectedItemID", queue.Items.Count > 0 ? queue.Items[queue.SelectedIndex].ItemId : 0)
            .Set("playQueueSelectedItemOffset", queue.SelectedIndex)
            .Set("playQueueSelectedMetadataItemID", queue.Items.Count > 0 ? _ids.GetOrAdd(IdMap.Item, queue.Items[queue.SelectedIndex].JellyfinId).ToString(CultureInfo.InvariantCulture) : null)
            .Set("playQueueShuffled", queue.Shuffled)
            .Set("playQueueSourceURI", queue.SourceUri)
            .Set("playQueueTotalCount", queue.Items.Count)
            .Set("playQueueVersion", queue.Version)
            .Set("machineIdentifier", Config.MachineIdentifier);

        for (var i = 0; i < nodes.Count; i++)
        {
            node.Add(nodes[i].Set("playQueueItemID", present[i].First.ItemId));
        }

        return ctx.Write(node.WithSize());
    }

    [GeneratedRegex(@"/library/metadata/(?<rk>\d+)")]
    private static partial Regex MetadataKey();
}
