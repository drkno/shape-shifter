using Jellyfin.Plugin.ShapeShifter.Data;
using Jellyfin.Plugin.ShapeShifter.Plex;
using Jellyfin.Plugin.ShapeShifter.Server;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShapeShifter.Api;

/// <summary>PMS collections over Jellyfin box sets. Changes need metadata-editor rights, as in PMS.</summary>
public sealed class CollectionEndpoints : IPlexEndpoints
{
    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly IdMap _ids;
    private readonly MetadataMapper _mapper;
    private readonly ILogger<CollectionEndpoints> _logger;

    public CollectionEndpoints(ILibraryManager libraryManager, ICollectionManager collectionManager, IdMap ids, MetadataMapper mapper, ILogger<CollectionEndpoints> logger)
    {
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _ids = ids;
        _mapper = mapper;
        _logger = logger;
    }

    public void Map(PlexRouter.IRouteBuilder routes)
    {
        routes.Post("/library/collections", Create)
            .Get("/library/collections/{id}", Get)
            .Delete("/library/collections/{id}", Delete)
            .Get("/library/collections/{id}/children", Children)
            .Get("/library/collections/{id}/items", Children)
            .Put("/library/collections/{id}/items", AddItems)
            .Delete("/library/collections/{id}/items/{item}", RemoveItem);
    }

    private BoxSet? Find(PlexContext ctx, string key)
    {
        var id = _ids.ResolveGuid(IdMap.Item, key);
        return id is not null && _libraryManager.GetItemById(id.Value) is BoxSet set && set.IsVisible(ctx.User) ? set : null;
    }

    private Task WriteCollection(PlexContext ctx, BoxSet set) =>
        ctx.Write(PlexNode.Container().Set("allowSync", false).Add(_mapper.Map(set, ctx.User, new MapOptions { Detailed = true, IncludeMedia = false })).WithSize());

    private Task Get(PlexContext ctx)
    {
        var set = Find(ctx, ctx.Route("id"));
        return set is null ? ctx.Status(404) : WriteCollection(ctx, set);
    }

    private Task Children(PlexContext ctx)
    {
        var set = Find(ctx, ctx.Route("id"));
        if (set is null)
        {
            return ctx.Status(404);
        }

        var children = set.GetLinkedChildren(ctx.User).OrderBy(c => c.PremiereDate ?? DateTime.MaxValue).ThenBy(c => c.SortName).ToList();
        var (start, size) = ctx.Paging();
        var rk = _mapper.RatingKey(set);
        var node = PlexNode.Container()
            .Set("allowSync", false)
            .Set("key", rk.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Set("title1", set.Name)
            .Set("viewGroup", children.FirstOrDefault() is { } first ? MetadataMapper.PlexType(first) : "movie")
            .Set("mediaTagPrefix", "/system/bundle/media/flags/")
            .Set("mediaTagVersion", 1);
        node.AddRange(_mapper.Map(children.Skip(start).Take(size ?? int.MaxValue).ToList(), ctx.User, new MapOptions()));
        return ctx.Write(node.WithSize(children.Count, start));
    }

    private async Task Create(PlexContext ctx)
    {
        if (!MetadataEditEndpoints.CanEdit(ctx))
        {
            await ctx.Status(403).ConfigureAwait(false);
            return;
        }

        var title = ctx.Query("title");
        if (string.IsNullOrWhiteSpace(title) || ctx.QueryBool("smart"))
        {
            await ctx.Status(400).ConfigureAwait(false);
            return;
        }

        var items = PlexUris.Items(ctx.Query("uri"), ctx.User, _ids, _libraryManager);
        var set = await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
        {
            Name = title,
            ItemIdList = items.Select(i => i.Id.ToString("N")).ToArray(),
            UserIds = [ctx.User.Id],
        }).ConfigureAwait(false);
        _logger.LogInformation("Created collection '{Title}' with {Count} items for {User}", title, items.Count, ctx.User.Username);
        await WriteCollection(ctx, set).ConfigureAwait(false);
    }

    private async Task Delete(PlexContext ctx)
    {
        var set = Find(ctx, ctx.Route("id"));
        if (set is null)
        {
            await ctx.Status(404).ConfigureAwait(false);
            return;
        }

        if (!MetadataEditEndpoints.CanEdit(ctx))
        {
            await ctx.Status(403).ConfigureAwait(false);
            return;
        }

        _libraryManager.DeleteItem(set, new DeleteOptions { DeleteFileLocation = true }, true);
        await ctx.WriteEmpty().ConfigureAwait(false);
    }

    private async Task AddItems(PlexContext ctx)
    {
        var set = Find(ctx, ctx.Route("id"));
        if (set is null)
        {
            await ctx.Status(404).ConfigureAwait(false);
            return;
        }

        if (!MetadataEditEndpoints.CanEdit(ctx))
        {
            await ctx.Status(403).ConfigureAwait(false);
            return;
        }

        var items = PlexUris.Items(ctx.Query("uri"), ctx.User, _ids, _libraryManager);
        if (items.Count > 0)
        {
            await _collectionManager.AddToCollectionAsync(set.Id, items.Select(i => i.Id)).ConfigureAwait(false);
        }

        await WriteCollection(ctx, set).ConfigureAwait(false);
    }

    private async Task RemoveItem(PlexContext ctx)
    {
        var set = Find(ctx, ctx.Route("id"));
        var item = _ids.ResolveGuid(IdMap.Item, ctx.Route("item"));
        if (set is null || item is null)
        {
            await ctx.Status(404).ConfigureAwait(false);
            return;
        }

        if (!MetadataEditEndpoints.CanEdit(ctx))
        {
            await ctx.Status(403).ConfigureAwait(false);
            return;
        }

        await _collectionManager.RemoveFromCollectionAsync(set.Id, [item.Value]).ConfigureAwait(false);
        await WriteCollection(ctx, set).ConfigureAwait(false);
    }
}
