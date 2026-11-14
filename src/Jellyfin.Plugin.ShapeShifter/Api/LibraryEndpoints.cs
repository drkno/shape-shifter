using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.ShapeShifter.Data;
using Jellyfin.Plugin.ShapeShifter.Plex;
using Jellyfin.Plugin.ShapeShifter.Server;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Jellyfin.Plugin.ShapeShifter.Library;

namespace Jellyfin.Plugin.ShapeShifter.Api;

/// <summary>Library browsing: sections, metadata, hubs, on deck, search.</summary>
public sealed partial class LibraryEndpoints : IPlexEndpoints
{
    private const int DefaultHubSize = 20;

    private readonly ILibraryManager _libraryManager;
    private readonly ITVSeriesManager _tvSeriesManager;
    private readonly IdMap _ids;
    private readonly MetadataMapper _mapper;
    private readonly IUserDataManager _userDataManager;
    private readonly Audiobooks _audiobooks;

    public LibraryEndpoints(ILibraryManager libraryManager, ITVSeriesManager tvSeriesManager, IdMap ids, MetadataMapper mapper, IUserDataManager userDataManager, Audiobooks audiobooks)
    {
        _audiobooks = audiobooks;
        _userDataManager = userDataManager;
        _libraryManager = libraryManager;
        _tvSeriesManager = tvSeriesManager;
        _ids = ids;
        _mapper = mapper;
    }

    public void Map(PlexRouter.IRouteBuilder routes)
    {
        routes.Get("/library", Library)
            .Get("/library/sections", Sections)
            .Get("/library/sections/{section}", SectionRoot)
            .Get("/library/sections/{section}/all", SectionAll)
            .Get("/library/sections/{section}/unwatched", ctx => SectionAll(ctx, unwatchedOnly: true))
            .Get("/library/sections/{section}/newest", ctx => SectionSorted(ctx, ItemSortBy.PremiereDate))
            .Get("/library/sections/{section}/recentlyAdded", ctx => SectionSorted(ctx, ItemSortBy.DateCreated))
            .Get("/library/sections/{section}/recentlyViewed", ctx => SectionSorted(ctx, ItemSortBy.DatePlayed))
            .Get("/library/sections/{section}/onDeck", ctx => OnDeck(ctx, ctx.Route("section")))
            .Get("/library/sections/{section}/genre", ctx => SectionTags(ctx, "genre"))
            .Get("/library/sections/{section}/year", ctx => SectionTags(ctx, "year"))
            .Get("/library/sections/{section}/filters", SectionFilters)
            .Get("/library/sections/{section}/sorts", SectionSorts)
            .Get("/library/sections/{section}/collections", SectionCollections)
            .Get("/library/sections/{section}/firstCharacter", FirstCharacter)
            .Get("/library/sections/{section}/categories", Categories)
            .Get("/library/sections/{section}/folder", Folder)
            .Get("/library/sections/{section}/contentRating", ctx => SectionValues(ctx, "contentRating"))
            .Get("/library/sections/{section}/studio", ctx => SectionValues(ctx, "studio"))
            .Get("/library/sections/{section}/decade", ctx => SectionValues(ctx, "decade"))
            .Get("/library/sections/{section}/label", ctx => SectionValues(ctx, "label"))
            .Get("/library/sections/{section}/prefs", ctx => ctx.WriteEmpty())
            .Get("/library/metadata/{ids}", Metadata)
            .Get("/library/metadata/{ids}/userState", UserState)
            .Get("/library/metadata/{id}/children", Children)
            .Get("/library/metadata/{id}/allLeaves", AllLeaves)
            .Get("/library/metadata/{id}/grandchildren", AllLeaves)
            .Get("/library/metadata/{id}/related", RelatedHubs)
            .Get("/library/metadata/{id}/similar", Similar)
            .Get("/hubs/metadata/{id}/related", RelatedHubs)
            .Get("/library/all", LibraryAll)
            .Get("/library/search", LibrarySearch)
            .Get("/library/people/{id}", Person)
            .Get("/library/people/{id}/media", PersonMedia)
            .Get("/library/metadata/{id}/extras", ctx => ctx.WriteEmpty())
            .Get("/library/onDeck", ctx => OnDeck(ctx, null))
            .Get("/library/recentlyAdded", RecentlyAdded)
            .Get("/hubs", Hubs)
            .Get("/hubs/promoted", Hubs)
            .Get("/hubs/home/onDeck", ctx => OnDeck(ctx, null))
            .Get("/hubs/home/continueWatching", ctx => ContinueWatchingHub(ctx))
            .Get("/hubs/continueWatching", ctx => ContinueWatchingHub(ctx))
            .Get("/hubs/continueWatching/items", ContinueWatchingItems)
            .Put("/actions/removeFromContinueWatching", RemoveFromContinueWatching)
            .Get("/hubs/home/recentlyAdded", RecentlyAdded)
            .Get("/hubs/sections/{section}", SectionHubs)
            .Get("/hubs/search", HubSearch)
            .Get("/search", Search);
    }

    internal static IEnumerable<CollectionFolder> VisibleSections(ILibraryManager libraryManager, User user) =>
        libraryManager.GetUserRootFolder()
            .GetChildren(user, true)
            .OfType<CollectionFolder>()
            .Where(IsPlexSection);

    /// <summary>Every library served as a Plex section, regardless of user (what plex.tv is told about).</summary>
    internal static IEnumerable<CollectionFolder> AllSections(ILibraryManager libraryManager) =>
        libraryManager.GetUserRootFolder().Children.OfType<CollectionFolder>().Where(IsPlexSection);

    private static bool IsPlexSection(CollectionFolder f) =>
        f.CollectionType is null or CollectionType.movies or CollectionType.tvshows or CollectionType.music
            or CollectionType.homevideos or CollectionType.musicvideos or CollectionType.photos or CollectionType.books;

    private CollectionFolder? FindSection(PlexContext ctx, string sectionKey)
    {
        var id = _ids.ResolveGuid(IdMap.Section, sectionKey);
        return id is null ? null : VisibleSections(_libraryManager, ctx.User).FirstOrDefault(s => s.Id == id);
    }

    private BaseItem? FindItem(PlexContext ctx, string ratingKey)
    {
        var id = _ids.ResolveGuid(IdMap.Item, ratingKey);
        if (id is null)
        {
            return null;
        }

        var item = _libraryManager.GetItemById(id.Value);
        return item is not null && item.IsVisible(ctx.User) ? item : null;
    }

    private Task Library(PlexContext ctx)
    {
        var node = PlexNode.Container().Set("title1", "Plex Library").Set("allowSync", false);
        node.Add(new PlexNode("Directory").Set("key", "sections").Set("title", "Library Sections"));
        node.Add(new PlexNode("Directory").Set("key", "recentlyAdded").Set("title", "Recently Added Content"));
        node.Add(new PlexNode("Directory").Set("key", "onDeck").Set("title", "On Deck Content"));
        return ctx.Write(node.WithSize());
    }

    private Task Sections(PlexContext ctx)
    {
        var node = PlexNode.Container().Set("allowSync", false).Set("title1", "Plex Library");
        foreach (var section in VisibleSections(_libraryManager, ctx.User))
        {
            node.Add(SectionNode(section));
        }

        return ctx.Write(node.WithSize());
    }

    private PlexNode SectionNode(CollectionFolder section)
    {
        var id = _mapper.SectionKey(section);
        var type = MetadataMapper.SectionType(section.CollectionType);
        var (agent, scanner) = MetadataMapper.AgentAndScanner(type);
        var updated = MetadataMapper.Unix(section.DateModified);
        var node = new PlexNode("Directory")
            .Set("allowSync", false)
            .Set("art", "/:/resources/movie-fanart.jpg")
            .Set("composite", $"/library/sections/{id}/composite/{updated}")
            .Set("filters", true)
            .Set("refreshing", false)
            .Set("thumb", "/:/resources/movie.png")
            .Set("key", id.ToString(CultureInfo.InvariantCulture))
            .Set("type", type)
            .Set("title", section.Name)
            .Set("agent", agent)
            .Set("scanner", scanner)
            .Set("language", "en-US")
            .Set("uuid", section.Id.ToString("D"))
            .Set("updatedAt", updated)
            .Set("createdAt", MetadataMapper.Unix(section.DateCreated))
            .Set("scannedAt", updated)
            .Set("content", true)
            .Set("directory", true)
            .Set("contentChangedAt", updated)
            .Set("hidden", 0);

        var locationId = 1;
        foreach (var path in section.PhysicalLocations)
        {
            node.Add(new PlexNode("Location").Set("id", locationId++).Set("path", path));
        }

        return node;
    }

    private Task SectionRoot(PlexContext ctx)
    {
        var section = FindSection(ctx, ctx.Route("section"));
        if (section is null)
        {
            return ctx.Status(404);
        }

        var node = PlexNode.Container()
            .Set("allowSync", false)
            .Set("content", "secondary")
            .Set("librarySectionID", _mapper.SectionKey(section))
            .Set("title1", section.Name)
            .Set("viewGroup", "secondary")
            .Set("viewMode", 65592);
        foreach (var (key, title) in new[] { ("all", "All"), ("unwatched", "Unwatched"), ("newest", "Recently Released"), ("recentlyAdded", "Recently Added"), ("recentlyViewed", "Recently Viewed"), ("onDeck", "On Deck"), ("genre", "By Genre"), ("year", "By Year"), ("collections", "Collections") })
        {
            node.Add(new PlexNode("Directory").Set("key", key).Set("title", title).Set("secondary", key is "genre" or "year" ? true : null));
        }

        return ctx.Write(node.WithSize());
    }

    private Task SectionAll(PlexContext ctx) => SectionAll(ctx, unwatchedOnly: false);

    private Task SectionAll(PlexContext ctx, bool unwatchedOnly)
    {
        var section = FindSection(ctx, ctx.Route("section"));
        if (section is null)
        {
            return ctx.Status(404);
        }

        if (Audiobooks.IsBooks(section))
        {
            return BooksSectionAll(ctx, section);
        }

        var query = BuildSectionQuery(ctx, section);
        if (unwatchedOnly || ctx.QueryBool("unwatched") || ctx.Query("unwatchedLeaves") == "1")
        {
            query.IsPlayed = false;
        }

        return WriteQuery(ctx, section, query, section.Name);
    }

    private Task SectionSorted(PlexContext ctx, ItemSortBy sortBy)
    {
        var section = FindSection(ctx, ctx.Route("section"));
        if (section is null)
        {
            return ctx.Status(404);
        }

        var query = BuildSectionQuery(ctx, section);
        query.OrderBy = [(sortBy, SortOrder.Descending)];
        if (sortBy == ItemSortBy.DatePlayed)
        {
            query.IsPlayed = true;
        }

        if (sortBy == ItemSortBy.DateCreated && section.CollectionType == CollectionType.tvshows && ctx.QueryInt("type") is null)
        {
            // PMS shows recently added episodes, not shows, for TV sections.
            query.IncludeItemTypes = [BaseItemKind.Episode];
        }

        return WriteQuery(ctx, section, query, section.Name);
    }

    private InternalItemsQuery BuildSectionQuery(PlexContext ctx, CollectionFolder section)
    {
        var (start, size) = ctx.Paging();
        var query = new InternalItemsQuery(ctx.User)
        {
            Parent = section,
            Recursive = true,
            IncludeItemTypes = MetadataMapper.KindsForPlexType(ctx.QueryInt("type")) ?? MetadataMapper.DefaultKindsForSection(section.CollectionType),
            StartIndex = start,
            Limit = size,
            EnableTotalRecordCount = true,
            OrderBy = ParseSort(ctx.Query("sort")),
            DtoOptions = new DtoOptions(true),
        };

        if (ctx.Query("genre") is { } genre)
        {
            // Our genre filter keys are genre item ratingKeys (see SectionTags).
            var genreIds = genre.Split(',').Select(g => _ids.ResolveGuid(IdMap.Item, g)).OfType<Guid>().ToArray();
            query.GenreIds = genreIds;
        }

        if (ctx.Query("year") is { } year)
        {
            query.Years = year.Split(',').Select(y => int.TryParse(y, out var v) ? v : 0).Where(v => v > 0).ToArray();
        }

        if (ctx.Query("decade") is { } decade && int.TryParse(decade, out var d))
        {
            query.Years = Enumerable.Range(d, 10).ToArray();
        }

        if (ctx.Query("title") is { } title)
        {
            query.SearchTerm = title;
        }

        if (ctx.Query("label") is { } label)
        {
            query.Tags = label.Split(',');
        }

        if (ctx.Query("studio") is { } studio)
        {
            query.StudioIds = studio.Split(',').Select(s => _libraryManager.GetStudio(s)?.Id).OfType<Guid>().ToArray();
        }

        if (ctx.Query("contentRating") is { } rating)
        {
            query.OfficialRatings = rating.Split(',');
        }

        return query;
    }

    private static (ItemSortBy, SortOrder)[] ParseSort(string? sort)
    {
        if (string.IsNullOrEmpty(sort))
        {
            return [(ItemSortBy.SortName, SortOrder.Ascending)];
        }

        var result = new List<(ItemSortBy, SortOrder)>();
        foreach (var part in sort.Split(','))
        {
            var pieces = part.Split(':');
            var order = pieces.Length > 1 && pieces[1].StartsWith("desc", StringComparison.OrdinalIgnoreCase) ? SortOrder.Descending : SortOrder.Ascending;
            ItemSortBy? by = pieces[0] switch
            {
                "titleSort" or "title" => ItemSortBy.SortName,
                "addedAt" => ItemSortBy.DateCreated,
                "originallyAvailableAt" => ItemSortBy.PremiereDate,
                "year" => ItemSortBy.ProductionYear,
                "lastViewedAt" => ItemSortBy.DatePlayed,
                "viewCount" => ItemSortBy.PlayCount,
                "rating" or "audienceRating" => ItemSortBy.CommunityRating,
                "duration" => ItemSortBy.Runtime,
                "contentRating" => ItemSortBy.OfficialRating,
                "random" => ItemSortBy.Random,
                "index" or "episode.index" => ItemSortBy.IndexNumber,
                _ => null,
            };
            if (by is not null)
            {
                result.Add((by.Value, order));
            }
        }

        if (result.Count == 0 || result[^1].Item1 != ItemSortBy.SortName)
        {
            result.Add((ItemSortBy.SortName, SortOrder.Ascending));
        }

        return result.ToArray();
    }

    private Task WriteQuery(PlexContext ctx, CollectionFolder section, InternalItemsQuery query, string title)
    {
        var result = section.GetItems(query);
        var node = PlexNode.Container()
            .Set("allowSync", false)
            .Set("librarySectionID", _mapper.SectionKey(section))
            .Set("librarySectionTitle", section.Name)
            .Set("librarySectionUUID", section.Id.ToString("D"))
            .Set("title1", title)
            .Set("viewGroup", MetadataMapper.SectionType(section.CollectionType))
            .Set("mediaTagPrefix", "/system/bundle/media/flags/")
            .Set("mediaTagVersion", 1);
        node.AddRange(_mapper.Map(result.Items, ctx.User, new MapOptions { Section = section }));
        return ctx.Write(node.WithSize(result.TotalRecordCount, query.StartIndex));
    }

    private Task SectionTags(PlexContext ctx, string kind)
    {
        var section = FindSection(ctx, ctx.Route("section"));
        if (section is null)
        {
            return ctx.Status(404);
        }

        var items = section.GetItems(new InternalItemsQuery(ctx.User)
        {
            Parent = section,
            Recursive = true,
            IncludeItemTypes = MetadataMapper.DefaultKindsForSection(section.CollectionType),
            DtoOptions = new DtoOptions(false),
        }).Items;

        var node = PlexNode.Container().Set("allowSync", false).Set("title1", section.Name).Set("title2", kind == "genre" ? "By Genre" : "By Year");
        if (kind == "genre")
        {
            foreach (var name in items.SelectMany(i => i.Genres).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            {
                var genre = section.CollectionType == CollectionType.music ? (BaseItem?)_libraryManager.GetMusicGenre(name) : _libraryManager.GetGenre(name);
                if (genre is not null)
                {
                    var key = _mapper.RatingKey(genre);
                    node.Add(new PlexNode("Directory").Set("key", key.ToString(CultureInfo.InvariantCulture)).Set("fastKey", $"/library/sections/{_mapper.SectionKey(section)}/all?genre={key}").Set("title", name).Set("type", "genre"));
                }
            }
        }
        else
        {
            foreach (var year in items.Select(i => i.ProductionYear).OfType<int>().Distinct().OrderDescending())
            {
                node.Add(new PlexNode("Directory").Set("key", year).Set("fastKey", $"/library/sections/{_mapper.SectionKey(section)}/all?year={year}").Set("title", year.ToString(CultureInfo.InvariantCulture)));
            }
        }

        return ctx.Write(node.WithSize());
    }

    private Task SectionFilters(PlexContext ctx)
    {
        var id = ctx.Route("section");
        var node = PlexNode.Container().Set("allowSync", false).Set("title1", "Filters");
        node.Add(new PlexNode("Directory").Set("filter", "genre").Set("filterType", "string").Set("key", $"/library/sections/{id}/genre").Set("title", "Genre").Set("type", "filter"));
        node.Add(new PlexNode("Directory").Set("filter", "year").Set("filterType", "integer").Set("key", $"/library/sections/{id}/year").Set("title", "Year").Set("type", "filter"));
        node.Add(new PlexNode("Directory").Set("filter", "unwatched").Set("filterType", "boolean").Set("key", $"/library/sections/{id}/unwatched").Set("title", "Unplayed").Set("type", "filter"));
        return ctx.Write(node.WithSize());
    }

    private static Task SectionSorts(PlexContext ctx)
    {
        var node = PlexNode.Container().Set("allowSync", false).Set("title1", "Sorts");
        foreach (var (key, title, defaultDir) in new[]
        {
            ("titleSort", "Title", "asc"), ("addedAt", "Date Added", "desc"), ("originallyAvailableAt", "Release Date", "desc"),
            ("year", "Year", "desc"), ("lastViewedAt", "Last Viewed", "desc"), ("audienceRating", "Rating", "desc"), ("duration", "Duration", "desc"),
        })
        {
            node.Add(new PlexNode("Directory").Set("defaultDirection", defaultDir).Set("descKey", $"{key}:desc").Set("key", key).Set("title", title));
        }

        return ctx.Write(node.WithSize());
    }

    private Task SectionCollections(PlexContext ctx)
    {
        var section = FindSection(ctx, ctx.Route("section"));
        if (section is null)
        {
            return ctx.Status(404);
        }

        // Jellyfin box sets live outside the section; show the ones that contain items from it.
        var sets = _libraryManager.GetItemList(new InternalItemsQuery(ctx.User)
        {
            IncludeItemTypes = [BaseItemKind.BoxSet],
            Recursive = true,
            OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)],
            DtoOptions = new DtoOptions(true),
        });
        var inSection = sets.OfType<BoxSet>()
            .Where(set => set.GetLinkedChildren(ctx.User).Take(5).Any(c => _libraryManager.GetCollectionFolders(c).Any(f => f.Id == section.Id)))
            .Cast<BaseItem>()
            .ToList();
        var (start, size) = ctx.Paging();
        var node = PlexNode.Container().Set("title1", section.Name).Set("title2", "Collections").Set("librarySectionID", _mapper.SectionKey(section));
        node.AddRange(_mapper.Map(inSection.Skip(start).Take(size ?? int.MaxValue).ToList(), ctx.User, new MapOptions { Section = section, IncludeMedia = false }));
        return ctx.Write(node.WithSize(inSection.Count, start));
    }

    private Task Metadata(PlexContext ctx)
    {
        var keys = ctx.Route("ids").Split(',');
        if (keys.Any(k => _audiobooks.Resolve(k) is not null))
        {
            return BooksMetadata(ctx, keys);
        }

        var items = keys.Select(id => FindItem(ctx, id)).OfType<BaseItem>().ToList();
        if (items.Count == 0)
        {
            return ctx.Status(404);
        }

        var node = PlexNode.Container().Set("allowSync", false).Set("identifier", MetadataMapper.LibraryIdentifier);
        node.AddRange(_mapper.Map(items, ctx.User, new MapOptions { Detailed = true }));
        if (items.Count == 1 && ctx.QueryBool("includeChildren") && items[0] is Folder)
        {
            node.Children[0].AddRange(_mapper.Map(GetChildren(ctx.User, items[0]), ctx.User, new MapOptions { IncludeMedia = false }));
        }

        return ctx.Write(node.WithSize());
    }

    private IReadOnlyList<BaseItem> GetChildren(User user, BaseItem item)
    {
        var dto = new DtoOptions(true);
        return item switch
        {
            Series series => series.GetSeasons(user, dto),
            Season season => season.GetEpisodes(user, dto, false),
            MusicArtist artist => _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = [BaseItemKind.MusicAlbum],
                ArtistIds = [artist.Id],
                Recursive = true,
                OrderBy = [(ItemSortBy.ProductionYear, SortOrder.Descending), (ItemSortBy.SortName, SortOrder.Ascending)],
                DtoOptions = dto,
            }),
            MusicAlbum album => album.Tracks.Where(t => t.IsVisible(user)).OrderBy(t => t.ParentIndexNumber ?? 1).ThenBy(t => t.IndexNumber ?? 0).ToList(),
            Folder folder => folder.GetItems(new InternalItemsQuery(user) { OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)], DtoOptions = dto }).Items,
            _ => [],
        };
    }

    private Task Children(PlexContext ctx)
    {
        if (_audiobooks.Resolve(ctx.Route("id")) is { } derived)
        {
            return BooksChildren(ctx, derived.Index, derived.Author, derived.Book, leaves: false);
        }

        var parent = FindItem(ctx, ctx.Route("id"));
        if (parent is null)
        {
            return ctx.Status(404);
        }

        var children = GetChildren(ctx.User, parent);
        var (start, size) = ctx.Paging();
        var page = children.Skip(start).Take(size ?? int.MaxValue).ToList();
        var node = ParentContainer(parent);
        node.AddRange(_mapper.Map(page, ctx.User, new MapOptions()));
        return ctx.Write(node.WithSize(children.Count, start));
    }

    private Task AllLeaves(PlexContext ctx)
    {
        if (_audiobooks.Resolve(ctx.Route("id")) is { } derived)
        {
            return BooksChildren(ctx, derived.Index, derived.Author, derived.Book, leaves: true);
        }

        var parent = FindItem(ctx, ctx.Route("id"));
        if (parent is null)
        {
            return ctx.Status(404);
        }

        var dto = new DtoOptions(true);
        IReadOnlyList<BaseItem> leaves = parent switch
        {
            Series series => series.GetEpisodes(ctx.User, dto, false).ToList(),
            Season season => season.GetEpisodes(ctx.User, dto, false),
            MusicArtist artist => _libraryManager.GetItemList(new InternalItemsQuery(ctx.User)
            {
                IncludeItemTypes = [BaseItemKind.Audio],
                ArtistIds = [artist.Id],
                Recursive = true,
                OrderBy = [(ItemSortBy.Album, SortOrder.Ascending), (ItemSortBy.ParentIndexNumber, SortOrder.Ascending), (ItemSortBy.IndexNumber, SortOrder.Ascending)],
                DtoOptions = dto,
            }),
            _ => GetChildren(ctx.User, parent),
        };

        var (start, size) = ctx.Paging();
        var page = leaves.Skip(start).Take(size ?? int.MaxValue).ToList();
        var node = ParentContainer(parent);
        node.AddRange(_mapper.Map(page, ctx.User, new MapOptions()));
        return ctx.Write(node.WithSize(leaves.Count, start));
    }

    private PlexNode ParentContainer(BaseItem parent)
    {
        var rk = _mapper.RatingKey(parent);
        var node = PlexNode.Container()
            .Set("allowSync", false)
            .Set("key", rk.ToString(CultureInfo.InvariantCulture))
            .Set("parentIndex", parent.IndexNumber)
            .Set("parentTitle", parent.Name)
            .Set("parentYear", parent.ProductionYear)
            .Set("title1", parent is Season s ? s.SeriesName : parent.Name)
            .Set("title2", parent is Season ? parent.Name : null)
            .Set("viewGroup", parent switch { Series => "season", Season => "episode", MusicArtist => "album", MusicAlbum => "track", _ => null })
            .Set("mediaTagPrefix", "/system/bundle/media/flags/")
            .Set("mediaTagVersion", 1);
        if (parent.HasImage(MediaBrowser.Model.Entities.ImageType.Primary))
        {
            node.Set("thumb", $"/library/metadata/{rk}/thumb/{MetadataMapper.Unix(parent.DateModified)}");
        }

        if (parent.HasImage(MediaBrowser.Model.Entities.ImageType.Backdrop))
        {
            node.Set("art", $"/library/metadata/{rk}/art/{MetadataMapper.Unix(parent.DateModified)}");
        }

        if (_libraryManager.GetCollectionFolders(parent).FirstOrDefault() is { } section)
        {
            node.Set("librarySectionID", _mapper.SectionKey(section)).Set("librarySectionTitle", section.Name).Set("librarySectionUUID", section.Id.ToString("D"));
        }

        return node;
    }

    /// <summary>Resume items followed by next-up episodes, like PMS's On Deck.</summary>
    private List<BaseItem> OnDeckItems(User user, CollectionFolder? section, int limit)
    {
        var dto = new DtoOptions(true);
        var resume = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IsResumable = true,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video],
            Recursive = true,
            Parent = section,
            OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)],
            Limit = limit,
            DtoOptions = dto,
        });

        var result = new List<BaseItem>(resume);
        if (result.Count < limit && (section is null || section.CollectionType == CollectionType.tvshows))
        {
            var nextUp = _tvSeriesManager.GetNextUp(
                new NextUpQuery
                {
                    User = user,
                    Limit = limit - result.Count,
                    ParentId = section?.Id,
                    EnableResumable = false,
                    EnableRewatching = false,
                },
                dto);
            var seenSeries = result.OfType<Episode>().Select(e => e.SeriesId).ToHashSet();
            result.AddRange(nextUp.Items.Where(i => i is not Episode e || !seenSeries.Contains(e.SeriesId)));
        }

        return result;
    }

    /// <summary>
    /// Per-item watch state, one UserState per requested id in order. Plex Web asks for this
    /// (and for watchlist state, which only plex.tv has) and copes with missing fields.
    /// </summary>
    private Task UserState(PlexContext ctx)
    {
        var node = PlexNode.Container();
        foreach (var key in ctx.Route("ids").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var id = _ids.ResolveGuid(IdMap.Item, key);
            var item = id is null ? null : _libraryManager.GetItemById(id.Value);
            var state = new PlexNode("UserState").Set("ratingKey", key);
            if (item is not null && item.IsVisible(ctx.User) && _userDataManager.GetUserData(ctx.User, item) is { } data)
            {
                state.Set("viewCount", data.Played ? Math.Max(1, data.PlayCount) : 0)
                    .Set("viewOffset", data.PlaybackPositionTicks > 0 ? MetadataMapper.Ms(data.PlaybackPositionTicks) : null)
                    .Set("lastViewedAt", data.LastPlayedDate is { } last ? MetadataMapper.Unix(last) : null)
                    .Set("viewState", data.Played ? "complete" : null);
            }

            node.Add(state);
        }

        return ctx.Write(node.WithSize());
    }

    private Task OnDeck(PlexContext ctx, string? sectionKey)
    {
        CollectionFolder? section = null;
        if (sectionKey is not null && (section = FindSection(ctx, sectionKey)) is null)
        {
            return ctx.Status(404);
        }

        var (_, size) = ctx.Paging();
        var items = OnDeckItems(ctx.User, section, size ?? 50);
        var node = PlexNode.Container().Set("allowSync", false).Set("identifier", MetadataMapper.LibraryIdentifier).Set("title1", "On Deck")
            .Set("mediaTagPrefix", "/system/bundle/media/flags/").Set("mediaTagVersion", 1);
        node.AddRange(_mapper.Map(items, ctx.User, new MapOptions { Section = section }));
        return ctx.Write(node.WithSize());
    }

    private static BaseItemKind[] RecentKinds(CollectionType? type) => type switch
    {
        CollectionType.tvshows => [BaseItemKind.Episode],
        CollectionType.music => [BaseItemKind.MusicAlbum],
        CollectionType.photos => [BaseItemKind.Photo],
        CollectionType.homevideos => [BaseItemKind.Video],
        _ => [BaseItemKind.Movie, BaseItemKind.Video],
    };

    private IReadOnlyList<BaseItem> RecentItems(User user, CollectionFolder? section, int limit) =>
        RecentItems(user, section is null ? [] : [section], section is null ? [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.MusicAlbum] : RecentKinds(section.CollectionType), 0, limit);

    private IReadOnlyList<BaseItem> RecentItems(User user, IReadOnlyList<CollectionFolder> sections, BaseItemKind[] kinds, int start, int limit) =>
        _libraryManager.GetItemList(RecentQuery(user, sections, kinds, start, limit));

    private static InternalItemsQuery RecentQuery(User user, IReadOnlyList<CollectionFolder> sections, BaseItemKind[] kinds, int start, int? limit) =>
        new InternalItemsQuery(user)
        {
            IncludeItemTypes = kinds,
            Recursive = true,
            AncestorIds = sections.Select(s => s.Id).ToArray(),
            IsVirtualItem = false,
            OrderBy = [(ItemSortBy.DateCreated, SortOrder.Descending)],
            StartIndex = start,
            Limit = limit,
            DtoOptions = new DtoOptions(true),
        };

    /// <summary>
    /// Home "Recently Added" hubs, one per media type across all of that type's libraries, with
    /// PMS's hub identifiers. Plex Web keys home hubs by identifier and server, and its home
    /// customisation only knows these names (home.movies.recent, home.television.recent, ...).
    /// </summary>
    private static readonly (int PlexType, string Identifier, string Title, string HubType, CollectionType[] Collections)[] HomeRecentHubs =
    [
        (1, "home.movies.recent", "Recently Added Movies", "movie", [CollectionType.movies]),
        (2, "home.television.recent", "Recently Added TV", "mixed", [CollectionType.tvshows]),
        (8, "home.music.recent", "Recently Added Music", "album", [CollectionType.music]),
        (13, "home.photos.recent", "Recently Added Photos", "photo", [CollectionType.photos]),
        (12, "home.videos.recent", "Recently Added Videos", "clip", [CollectionType.homevideos]),
    ];

    private IReadOnlyList<CollectionFolder> SectionsOfType(User user, CollectionType[] collections, string? sectionKey)
    {
        var sections = VisibleSections(_libraryManager, user).Where(s => s.CollectionType is { } t && collections.Contains(t));
        if (!string.IsNullOrEmpty(sectionKey))
        {
            var wanted = sectionKey.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
            sections = sections.Where(s => wanted.Contains(_mapper.SectionKey(s).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return sections.ToList();
    }

    private Task RecentlyAdded(PlexContext ctx)
    {
        var (start, size) = ctx.Paging();
        var limit = size ?? ctx.QueryInt("count") ?? 50;
        var plexType = ctx.QueryInt("type");
        var hub = HomeRecentHubs.FirstOrDefault(h => h.PlexType == plexType || (plexType == 4 && h.PlexType == 2));
        var node = PlexNode.Container().Set("allowSync", false).Set("identifier", MetadataMapper.LibraryIdentifier).Set("title1", "Recently Added")
            .Set("mediaTagPrefix", "/system/bundle/media/flags/").Set("mediaTagVersion", 1);
        var (sections, kinds) = hub.Identifier is null
            ? (VisibleSections(_libraryManager, ctx.User).ToList(), new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.MusicAlbum })
            : (SectionsOfType(ctx.User, hub.Collections, ctx.Query("sectionID")), RecentKinds(hub.Collections[0]));
        if (sections.Count == 0)
        {
            return ctx.Write(node.Set("size", 0).Set("offset", start).Set("totalSize", 0));
        }

        var items = RecentItems(ctx.User, sections, kinds, start, limit);
        var total = _libraryManager.GetCount(RecentQuery(ctx.User, sections, kinds, 0, null));
        node.AddRange(_mapper.Map(items, ctx.User, new MapOptions()));
        node.WithSize();
        return ctx.Write(node.Set("offset", start).Set("totalSize", total));
    }

    private PlexNode Hub(string identifier, string title, string key, string type, IReadOnlyList<PlexNode> items, bool more, string context, bool promoted = false) =>
        new PlexNode("Hub", "Hub")
            .Set("hubKey", key)
            .Set("key", key)
            .Set("title", title)
            .Set("type", type)
            .Set("hubIdentifier", identifier)
            .Set("context", context)
            .Set("size", items.Count)
            .Set("more", more)
            .Set("style", "shelf")
            .Set("promoted", promoted ? true : null)
            .AddRange(items);

    private Task ContinueWatchingHub(PlexContext ctx)
    {
        var size = ctx.QueryInt("count") ?? ctx.Paging().Size ?? DefaultHubSize;
        var items = _mapper.Map(OnDeckItems(ctx.User, null, size), ctx.User, new MapOptions());
        var node = PlexNode.Container().Set("allowSync", false).Set("identifier", MetadataMapper.LibraryIdentifier);
        node.Add(Hub("home.continue", "Continue Watching", "/hubs/home/continueWatching", "mixed", items, false, "hub.home.continue", true));
        return ctx.Write(node.WithSize());
    }

    /// <summary>The Continue Watching hub's items as a plain list (what newer apps page through).</summary>
    private Task ContinueWatchingItems(PlexContext ctx)
    {
        var (start, size) = ctx.Paging();
        var items = OnDeckItems(ctx.User, null, start + (size ?? DefaultHubSize));
        var node = PlexNode.Container().Set("allowSync", false).Set("identifier", MetadataMapper.LibraryIdentifier)
            .Set("hubIdentifier", "home.continue").Set("title", "Continue Watching");
        node.AddRange(_mapper.Map(items.Skip(start).ToList(), ctx.User, new MapOptions()));
        return ctx.Write(node.WithSize(null, start));
    }

    /// <summary>Drops an item from Continue Watching by clearing its resume point, as PMS does.</summary>
    private Task RemoveFromContinueWatching(PlexContext ctx)
    {
        var item = FindItem(ctx, ctx.Query("ratingKey") ?? ctx.Query("key")?.Split('/').LastOrDefault() ?? string.Empty);
        if (item is null)
        {
            return ctx.Status(404);
        }

        if (_userDataManager.GetUserData(ctx.User, item) is { } data)
        {
            data.PlaybackPositionTicks = 0;
            _userDataManager.SaveUserData(ctx.User, item, data, UserDataSaveReason.UpdateUserData, ctx.Http.RequestAborted);
        }

        return ctx.WriteEmpty();
    }

    private Task Hubs(PlexContext ctx)
    {
        var count = ctx.QueryInt("count") ?? DefaultHubSize;
        var user = ctx.User;
        var node = PlexNode.Container().Set("allowSync", false).Set("identifier", MetadataMapper.LibraryIdentifier);

        // ?identifier=home.continue,home.ondeck asks for just those hubs (Plezy's Continue Watching).
        var wanted = ctx.Query("identifier")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        bool Want(string identifier) => wanted is null || wanted.Contains(identifier);

        if (Want("home.continue") || Want("home.ondeck"))
        {
            var deck = _mapper.Map(OnDeckItems(user, null, count), user, new MapOptions());
            node.Add(Hub("home.continue", "Continue Watching", "/hubs/home/continueWatching", "mixed", deck, false, "hub.home.continue", true));
        }

        foreach (var hub in HomeRecentHubs.Where(h => Want(h.Identifier)))
        {
            var sections = SectionsOfType(user, hub.Collections, null);
            if (sections.Count == 0)
            {
                continue;
            }

            var recent = _mapper.Map(RecentItems(user, sections, RecentKinds(hub.Collections[0]), 0, count), user, new MapOptions());
            if (recent.Count > 0)
            {
                var key = $"/hubs/home/recentlyAdded?type={hub.PlexType}";
                node.Add(Hub(hub.Identifier, hub.Title, key, hub.HubType, recent, recent.Count >= count, "hub." + hub.Identifier, true));
            }
        }

        return ctx.Write(node.WithSize());
    }

    private Task SectionHubs(PlexContext ctx)
    {
        var section = FindSection(ctx, ctx.Route("section"));
        if (section is null)
        {
            return ctx.Status(404);
        }

        if (Audiobooks.IsBooks(section))
        {
            return BooksSectionHubs(ctx, section);
        }

        var count = ctx.QueryInt("count") ?? DefaultHubSize;
        var id = _mapper.SectionKey(section);
        var options = new MapOptions { Section = section };
        var node = PlexNode.Container().Set("allowSync", false).Set("librarySectionID", id).Set("librarySectionTitle", section.Name).Set("librarySectionUUID", section.Id.ToString("D"));

        var deck = _mapper.Map(OnDeckItems(ctx.User, section, count), ctx.User, options);
        if (deck.Count > 0)
        {
            node.Add(Hub($"{MetadataMapper.SectionType(section.CollectionType)}.inprogress.{id}", "Continue Watching", $"/library/sections/{id}/onDeck", "mixed", deck, false, "hub.continue"));
        }

        var recent = _mapper.Map(RecentItems(ctx.User, section, count), ctx.User, options);
        node.Add(Hub($"{MetadataMapper.SectionType(section.CollectionType)}.recentlyadded.{id}", "Recently Added", $"/library/sections/{id}/recentlyAdded", MetadataMapper.SectionType(section.CollectionType), recent, recent.Count >= count, "hub.recentlyadded"));
        return ctx.Write(node.WithSize());
    }

    private IReadOnlyList<BaseItem> SearchItems(User user, string term, int limit, BaseItemKind[] kinds) =>
        _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            SearchTerm = term,
            IncludeItemTypes = kinds,
            Recursive = true,
            Limit = limit,
            DtoOptions = new DtoOptions(true),
        });

    private Task HubSearch(PlexContext ctx)
    {
        var term = ctx.Query("query") ?? string.Empty;
        var limit = ctx.QueryInt("limit") ?? 10;
        var node = PlexNode.Container().Set("size", 0);
        if (term.Length == 0)
        {
            return ctx.Write(node);
        }

        foreach (var (title, type, kinds) in new (string, string, BaseItemKind[])[]
        {
            ("Movies", "movie", [BaseItemKind.Movie]),
            ("Shows", "show", [BaseItemKind.Series]),
            ("Episodes", "episode", [BaseItemKind.Episode]),
            ("Artists", "artist", [BaseItemKind.MusicArtist]),
            ("Albums", "album", [BaseItemKind.MusicAlbum]),
            ("Tracks", "track", [BaseItemKind.Audio]),
        })
        {
            var items = _mapper.Map(SearchItems(ctx.User, term, limit, kinds), ctx.User, new MapOptions());
            node.Add(Hub(type, title, $"/hubs/search?query={Uri.EscapeDataString(term)}", type, items, false, $"hub.{type}"));
        }

        return ctx.Write(node.WithSize());
    }

    private Task Search(PlexContext ctx)
    {
        var term = ctx.Query("query") ?? string.Empty;
        var items = term.Length == 0 ? [] : SearchItems(ctx.User, term, ctx.QueryInt("limit") ?? 50, [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode, BaseItemKind.MusicArtist, BaseItemKind.MusicAlbum, BaseItemKind.Audio]);
        var node = PlexNode.Container().Set("identifier", MetadataMapper.LibraryIdentifier);
        node.AddRange(_mapper.Map(items, ctx.User, new MapOptions()));
        return ctx.Write(node.WithSize());
    }
}
