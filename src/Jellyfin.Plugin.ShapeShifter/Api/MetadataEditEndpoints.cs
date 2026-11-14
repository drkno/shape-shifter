using System.Globalization;
using System.Web;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.ShapeShifter.Configuration;
using Jellyfin.Plugin.ShapeShifter.Data;
using Jellyfin.Plugin.ShapeShifter.Plex;
using Jellyfin.Plugin.ShapeShifter.Server;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShapeShifter.Api;

/// <summary>
/// Metadata editing from Plex clients: field and tag edits, locks, artwork, refresh, matching
/// and deletion. Allowed for Jellyfin administrators and configured metadata editors.
/// </summary>
public sealed class MetadataEditEndpoints : IPlexEndpoints
{
    private static readonly Dictionary<string, MetadataField> FieldLocks = new(StringComparer.Ordinal)
    {
        ["title"] = MetadataField.Name,
        ["summary"] = MetadataField.Overview,
        ["studio"] = MetadataField.Studios,
        ["contentRating"] = MetadataField.OfficialRating,
        ["genre"] = MetadataField.Genres,
        ["label"] = MetadataField.Tags,
        ["country"] = MetadataField.ProductionLocations,
        ["actor"] = MetadataField.Cast,
        ["director"] = MetadataField.Cast,
        ["writer"] = MetadataField.Cast,
        ["producer"] = MetadataField.Cast,
    };

    private static readonly Dictionary<string, PersonKind> PeopleTags = new(StringComparer.Ordinal)
    {
        ["actor"] = PersonKind.Actor,
        ["director"] = PersonKind.Director,
        ["writer"] = PersonKind.Writer,
        ["producer"] = PersonKind.Producer,
    };

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly ICollectionManager _collectionManager;
    private readonly IFileSystem _fileSystem;
    private readonly IdMap _ids;
    private readonly MetadataMapper _mapper;
    private readonly ILogger<MetadataEditEndpoints> _logger;

    public MetadataEditEndpoints(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        ICollectionManager collectionManager,
        IFileSystem fileSystem,
        IdMap ids,
        MetadataMapper mapper,
        ILogger<MetadataEditEndpoints> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _collectionManager = collectionManager;
        _fileSystem = fileSystem;
        _ids = ids;
        _mapper = mapper;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    public void Map(PlexRouter.IRouteBuilder routes)
    {
        routes.Put("/library/sections/{section}/all", ctx => Edit(ctx, ctx.Query("id")))
            .Put("/library/metadata/{ids}", ctx => Edit(ctx, ctx.Route("ids")))
            .Get("/library/metadata/{id}/posters", ctx => ListImages(ctx, ImageType.Primary))
            .Get("/library/metadata/{id}/arts", ctx => ListImages(ctx, ImageType.Backdrop))
            .Post("/library/metadata/{id}/posters", ctx => Upload(ctx, ImageType.Primary))
            .Post("/library/metadata/{id}/arts", ctx => Upload(ctx, ImageType.Backdrop))
            .Put("/library/metadata/{id}/poster", ctx => Select(ctx, ImageType.Primary))
            .Put("/library/metadata/{id}/art", ctx => Select(ctx, ImageType.Backdrop))
            .Put("/library/metadata/{id}/refresh", Refresh)
            .Get("/library/sections/{section}/refresh", ScanLibrary)
            .Put("/library/sections/{section}/refresh", ScanLibrary)
            .Put("/library/metadata/{id}/prefs", ItemPrefs)
            .Get("/library/metadata/{id}/matches", Matches)
            .Put("/library/metadata/{id}/match", Match)
            .Put("/library/metadata/{id}/unmatch", Unmatch)
            .Delete("/library/metadata/{id}", Delete);
    }

    /// <summary>Jellyfin admins plus users listed in MetadataEditors (by Jellyfin id, Plex account id or Plex username).</summary>
    /// <summary>
    /// Per-item preferences (episode ordering, auto-delete, ...) have no Jellyfin equivalent.
    /// Accept them from editors so clients don't report a failure.
    /// </summary>
    private static Task ItemPrefs(PlexContext ctx) => CanEdit(ctx) ? ctx.WriteEmpty() : ctx.Status(403);

    public static bool CanEdit(PlexContext ctx)
    {
        var p = ctx.Principal!;
        if (p.User.HasPermission(PermissionKind.IsAdministrator))
        {
            return true;
        }

        return Config.MetadataEditors.Any(e =>
            (Guid.TryParse(e, out var id) && id == p.User.Id)
            || e == p.PlexAccountId
            || string.Equals(e, p.PlexUsername, StringComparison.OrdinalIgnoreCase));
    }

    private BaseItem? Item(PlexContext ctx, string? ratingKey)
    {
        var id = _ids.ResolveGuid(IdMap.Item, ratingKey);
        var item = id is null ? null : _libraryManager.GetItemById(id.Value);
        return item is not null && item.IsVisible(ctx.User) ? item : null;
    }

    /// <summary>Runs an editing handler after the permission and item checks.</summary>
    private async Task Guarded(PlexContext ctx, Func<BaseItem, Task> action)
    {
        if (!CanEdit(ctx))
        {
            await ctx.Status(403).ConfigureAwait(false);
            return;
        }

        var item = Item(ctx, ctx.Route("id"));
        if (item is null)
        {
            await ctx.Status(404).ConfigureAwait(false);
            return;
        }

        await action(item).ConfigureAwait(false);
    }

    private async Task Edit(PlexContext ctx, string? ids)
    {
        if (!CanEdit(ctx))
        {
            await ctx.Status(403).ConfigureAwait(false);
            return;
        }

        var items = (ids ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(id => Item(ctx, id)).OfType<BaseItem>().ToList();
        if (items.Count == 0)
        {
            await ctx.Status(404).ConfigureAwait(false);
            return;
        }

        var edit = MetadataEditRequest.Parse(ctx.Http.Request.Query.Select(q => new KeyValuePair<string, string?>(q.Key, q.Value.ToString())));
        foreach (var item in items)
        {
            await ApplyAsync(item, edit, ctx.Http.RequestAborted).ConfigureAwait(false);
            _logger.LogInformation("{User} edited metadata of '{Item}' from {Client}", ctx.Principal!.PlexUsername, item.Name, ctx.Product);
        }

        await ctx.WriteEmpty().ConfigureAwait(false);
    }

    internal async Task ApplyAsync(BaseItem item, MetadataEditRequest edit, CancellationToken ct)
    {
        foreach (var (field, value) in edit.Values)
        {
            ApplyField(item, field, value);
        }

        foreach (var field in edit.TagAdds.Keys.Concat(edit.TagRemoves.Keys).Distinct().Where(f => !PeopleTags.ContainsKey(f) && f != "collection"))
        {
            var current = TagList(item, field);
            if (current is null)
            {
                continue;
            }

            var updated = current
                .Concat(edit.TagAdds.GetValueOrDefault(field) ?? [])
                .Where(t => !(edit.TagRemoves.GetValueOrDefault(field)?.Contains(t, StringComparer.OrdinalIgnoreCase) ?? false))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            SetTagList(item, field, updated);
        }

        // Plex auto-locks edited fields; mirror the explicit lock flags onto Jellyfin's locks.
        var locks = item.LockedFields.ToHashSet();
        foreach (var (field, locked) in edit.Locks)
        {
            if (FieldLocks.TryGetValue(field, out var lockField))
            {
                if (locked)
                {
                    locks.Add(lockField);
                }
                else
                {
                    locks.Remove(lockField);
                }
            }
        }

        item.LockedFields = locks.ToArray();
        await _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);

        await ApplyPeopleAsync(item, edit, ct).ConfigureAwait(false);
        await ApplyCollectionsAsync(item, edit).ConfigureAwait(false);
    }

    private static void ApplyField(BaseItem item, string field, string value)
    {
        var empty = value.Length == 0;
        switch (field)
        {
            case "title" when !empty: item.Name = value; break;
            case "titleSort": item.ForcedSortName = empty ? null : value; break;
            case "originalTitle": item.OriginalTitle = empty ? null : value; break;
            case "summary": item.Overview = empty ? null : value; break;
            case "tagline": item.Tagline = empty ? null : value; break;
            case "studio": item.Studios = empty ? [] : [value]; break;
            case "contentRating": item.OfficialRating = empty ? null : value; break;
            case "originallyAvailableAt":
                if (empty)
                {
                    item.PremiereDate = null;
                }
                else if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date))
                {
                    item.PremiereDate = date;
                    item.ProductionYear = date.Year;
                }

                break;
            case "year": item.ProductionYear = int.TryParse(value, out var year) ? year : null; break;
            case "audienceRating": item.CommunityRating = float.TryParse(value, CultureInfo.InvariantCulture, out var ar) ? ar : null; break;
            case "rating": item.CriticRating = float.TryParse(value, CultureInfo.InvariantCulture, out var cr) ? cr * 10 : null; break;
            case "index": item.IndexNumber = int.TryParse(value, out var idx) ? idx : null; break;
            case "parentIndex": item.ParentIndexNumber = int.TryParse(value, out var pidx) ? pidx : null; break;
        }
    }

    private static string[]? TagList(BaseItem item, string field) => field switch
    {
        "genre" => item.Genres,
        "label" or "mood" or "style" => item.Tags,
        "country" => item.ProductionLocations,
        "studio" => item.Studios,
        _ => null,
    };

    private static void SetTagList(BaseItem item, string field, string[] values)
    {
        switch (field)
        {
            case "genre": item.Genres = values; break;
            case "label" or "mood" or "style": item.Tags = values; break;
            case "country": item.ProductionLocations = values; break;
            case "studio": item.Studios = values; break;
        }
    }

    private async Task ApplyPeopleAsync(BaseItem item, MetadataEditRequest edit, CancellationToken ct)
    {
        if (!edit.TagAdds.Keys.Concat(edit.TagRemoves.Keys).Any(PeopleTags.ContainsKey))
        {
            return;
        }

        var people = _libraryManager.GetPeople(item).ToList();
        foreach (var (field, kind) in PeopleTags)
        {
            if (edit.TagRemoves.TryGetValue(field, out var removes))
            {
                people.RemoveAll(p => p.Type == kind && removes.Contains(p.Name, StringComparer.OrdinalIgnoreCase));
            }

            foreach (var name in edit.TagAdds.GetValueOrDefault(field) ?? [])
            {
                if (!people.Any(p => p.Type == kind && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    people.Add(new PersonInfo { Name = name, Type = kind });
                }
            }
        }

        await _libraryManager.UpdatePeopleAsync(item, people, ct).ConfigureAwait(false);
    }

    /// <summary>Plex collections are tags; in Jellyfin they're box sets, created on demand.</summary>
    private async Task ApplyCollectionsAsync(BaseItem item, MetadataEditRequest edit)
    {
        foreach (var name in edit.TagAdds.GetValueOrDefault("collection") ?? [])
        {
            if (FindBoxSet(name) is { } set)
            {
                await _collectionManager.AddToCollectionAsync(set.Id, [item.Id]).ConfigureAwait(false);
            }
            else
            {
                await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions { Name = name, ItemIdList = [item.Id.ToString("N")] }).ConfigureAwait(false);
            }
        }

        foreach (var name in edit.TagRemoves.GetValueOrDefault("collection") ?? [])
        {
            if (FindBoxSet(name) is { } set)
            {
                await _collectionManager.RemoveFromCollectionAsync(set.Id, [item.Id]).ConfigureAwait(false);
            }
        }
    }

    private BoxSet? FindBoxSet(string name) =>
        _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.BoxSet], Recursive = true, Name = name })
            .OfType<BoxSet>()
            .FirstOrDefault();

    private Task ListImages(PlexContext ctx, ImageType type) => Guarded(ctx, async item =>
    {
        var rk = _mapper.RatingKey(item);
        var node = PlexNode.Container().Set("identifier", MetadataMapper.LibraryIdentifier).Set("mediaTagPrefix", "/system/bundle/media/flags/");
        if (item.HasImage(type))
        {
            var own = $"/library/metadata/{rk}/{(type == ImageType.Primary ? "thumb" : "art")}/{MetadataMapper.Unix(item.DateModified)}";
            node.Add(new PlexNode("Photo", "Metadata").Set("key", own).Set("ratingKey", own).Set("selected", true).Set("thumb", own).Set("provider", "local"));
        }

        var remote = await _providerManager.GetAvailableRemoteImages(item, new RemoteImageQuery("") { ImageType = type, IncludeAllLanguages = true }, ctx.Http.RequestAborted).ConfigureAwait(false);
        foreach (var image in remote.Take(60))
        {
            node.Add(new PlexNode("Photo", "Metadata")
                .Set("key", image.Url)
                .Set("ratingKey", image.Url)
                .Set("selected", false)
                .Set("thumb", image.ThumbnailUrl ?? image.Url)
                .Set("provider", image.ProviderName));
        }

        await ctx.Write(node.WithSize()).ConfigureAwait(false);
    });

    private Task Select(PlexContext ctx, ImageType type) => Guarded(ctx, async item =>
    {
        var url = ctx.Query("url");
        if (url is null || url.StartsWith("/library/metadata/", StringComparison.Ordinal))
        {
            // Re-selecting the current local image.
            await ctx.WriteEmpty().ConfigureAwait(false);
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            await ctx.Status(400).ConfigureAwait(false);
            return;
        }

        await _providerManager.SaveImage(item, url, type, null, ctx.Http.RequestAborted).ConfigureAwait(false);
        await _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.ImageUpdate, ctx.Http.RequestAborted).ConfigureAwait(false);
        await ctx.WriteEmpty().ConfigureAwait(false);
    });

    private Task Upload(PlexContext ctx, ImageType type) => Guarded(ctx, async item =>
    {
        if (ctx.Query("url") is not null)
        {
            await Select(ctx, type).ConfigureAwait(false);
            return;
        }

        using var buffer = new MemoryStream();
        await ctx.Http.Request.Body.CopyToAsync(buffer, ctx.Http.RequestAborted).ConfigureAwait(false);
        var mime = SniffImageType(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        if (mime is null)
        {
            await ctx.Status(400).ConfigureAwait(false);
            return;
        }

        buffer.Position = 0;
        await _providerManager.SaveImage(item, buffer, mime, type, null, ctx.Http.RequestAborted).ConfigureAwait(false);
        await _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.ImageUpdate, ctx.Http.RequestAborted).ConfigureAwait(false);
        await ctx.WriteEmpty().ConfigureAwait(false);
    });

    internal static string? SniffImageType(ReadOnlySpan<byte> data) => data switch
    {
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [0x89, (byte)'P', (byte)'N', (byte)'G', ..] => "image/png",
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..] => "image/webp",
        [(byte)'G', (byte)'I', (byte)'F', ..] => "image/gif",
        _ => null,
    };

    private MetadataRefreshOptions RefreshOptions(bool replaceAll) => new(new DirectoryService(_fileSystem))
    {
        MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
        ImageRefreshMode = MetadataRefreshMode.FullRefresh,
        ReplaceAllMetadata = replaceAll,
        ReplaceAllImages = replaceAll,
    };

    private Task Refresh(PlexContext ctx) => Guarded(ctx, async item =>
    {
        _providerManager.QueueRefresh(item.Id, RefreshOptions(ctx.QueryBool("force")), RefreshPriority.High);
        await ctx.WriteEmpty().ConfigureAwait(false);
    });

    private async Task ScanLibrary(PlexContext ctx)
    {
        if (!CanEdit(ctx))
        {
            await ctx.Status(403).ConfigureAwait(false);
            return;
        }

        _libraryManager.QueueLibraryScan();
        await ctx.WriteEmpty().ConfigureAwait(false);
    }

    private Task Matches(PlexContext ctx) => Guarded(ctx, async item =>
    {
        var title = ctx.Query("title") ?? item.Name;
        var year = ctx.QueryInt("year") ?? item.ProductionYear;
        IEnumerable<RemoteSearchResult> results = item switch
        {
            Movie => await _providerManager.GetRemoteSearchResults<Movie, MovieInfo>(
                new RemoteSearchQuery<MovieInfo> { ItemId = item.Id, SearchInfo = new MovieInfo { Name = title, Year = year } }, ctx.Http.RequestAborted).ConfigureAwait(false),
            Series => await _providerManager.GetRemoteSearchResults<Series, SeriesInfo>(
                new RemoteSearchQuery<SeriesInfo> { ItemId = item.Id, SearchInfo = new SeriesInfo { Name = title, Year = year } }, ctx.Http.RequestAborted).ConfigureAwait(false),
            _ => [],
        };

        var node = PlexNode.Container();
        var score = 100;
        foreach (var result in results.Take(20))
        {
            node.Add(new PlexNode("SearchResult")
                .Set("guid", MatchGuid(result.ProviderIds))
                .Set("name", result.Name)
                .Set("year", result.ProductionYear)
                .Set("summary", result.Overview)
                .Set("thumb", result.ImageUrl)
                .Set("score", Math.Max(1, score))
                .Set("matched", item.ProviderIds.Count > 0 && result.ProviderIds.Any(p => item.ProviderIds.TryGetValue(p.Key, out var v) && v == p.Value)));
            score -= 5;
        }

        await ctx.Write(node.WithSize()).ConfigureAwait(false);
    });

    /// <summary>Encodes provider ids as the opaque match guid the client echoes back to /match.</summary>
    internal static string MatchGuid(IReadOnlyDictionary<string, string> providerIds) =>
        "shapeshifter://match?" + string.Join('&', providerIds.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

    internal static Dictionary<string, string> ParseMatchGuid(string guid)
    {
        var q = guid.IndexOf('?', StringComparison.Ordinal);
        var parsed = HttpUtility.ParseQueryString(q < 0 ? string.Empty : guid[(q + 1)..]);
        return parsed.AllKeys.OfType<string>().ToDictionary(k => k, k => parsed[k]!, StringComparer.OrdinalIgnoreCase);
    }

    private Task Match(PlexContext ctx) => Guarded(ctx, async item =>
    {
        var guid = ctx.Query("guid");
        var providerIds = guid is null ? [] : ParseMatchGuid(guid);
        if (providerIds.Count == 0)
        {
            await ctx.Status(400).ConfigureAwait(false);
            return;
        }

        item.ProviderIds = providerIds;
        await _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, ctx.Http.RequestAborted).ConfigureAwait(false);
        await _providerManager.RefreshFullItem(item, RefreshOptions(true), ctx.Http.RequestAborted).ConfigureAwait(false);
        _logger.LogInformation("{User} re-matched '{Item}' to {Ids}", ctx.Principal!.PlexUsername, item.Name, string.Join(", ", providerIds.Select(p => $"{p.Key}={p.Value}")));
        await ctx.WriteEmpty().ConfigureAwait(false);
    });

    private Task Unmatch(PlexContext ctx) => Guarded(ctx, async item =>
    {
        item.ProviderIds = [];
        await _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, ctx.Http.RequestAborted).ConfigureAwait(false);
        await ctx.WriteEmpty().ConfigureAwait(false);
    });

    private Task Delete(PlexContext ctx) => Guarded(ctx, async item =>
    {
        if (!Config.AllowMediaDeletion)
        {
            await ctx.Status(403).ConfigureAwait(false);
            return;
        }

        _logger.LogWarning("{User} deleted '{Item}' ({Path}) from {Client}", ctx.Principal!.PlexUsername, item.Name, item.Path, ctx.Product);
        _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true }, true);
        await ctx.WriteEmpty().ConfigureAwait(false);
    });
}
