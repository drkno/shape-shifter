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
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Jellyfin.Plugin.ShapeShifter.Library;

namespace Jellyfin.Plugin.ShapeShifter.Api;

/// <summary>
/// Cross-library lookups and browsing extras: /library/all, /library/search, people, related
/// hubs, folders, categories and the alphabet bar.
/// </summary>
public sealed partial class LibraryEndpoints
{
    private Guid[] SectionIds(User user) => VisibleSections(_libraryManager, user).Select(s => s.Id).ToArray();

    /// <summary>
    /// PMS's library-wide filter endpoint. Supported filters: type, guid (imdb://, tmdb://, tvdb://),
    /// show.id, season.index, title/title=, year, plus paging.
    /// </summary>
    private Task LibraryAll(PlexContext ctx)
    {
        var user = ctx.User;
        var plexType = ctx.QueryInt("type");
        var kinds = MetadataMapper.KindsForPlexType(plexType) ?? [BaseItemKind.Movie, BaseItemKind.Series];
        var (start, size) = ctx.Paging();

        InternalItemsQuery Query() => new(user)
        {
            IncludeItemTypes = kinds,
            Recursive = true,
            AncestorIds = SectionIds(user),
            IsVirtualItem = false,
            OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)],
            DtoOptions = new DtoOptions(true),
        };

        var results = new List<BaseItem>();
        if (ctx.Query("guid") is { Length: > 0 } guids)
        {
            // One provider-id query per guid: HasAnyProviderId takes one value per provider.
            foreach (var guid in guids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (ProviderId(guid) is not { } provider)
                {
                    continue;
                }

                var q = Query();
                q.HasAnyProviderId = new Dictionary<string, string> { [provider.Name] = provider.Value };
                results.AddRange(_libraryManager.GetItemList(q));
            }
        }
        else
        {
            var q = Query();
            if (ctx.Query("show.id") is { Length: > 0 } shows)
            {
                q.AncestorIds = shows.Split(',').Select(k => _ids.ResolveGuid(IdMap.Item, k)).OfType<Guid>().ToArray();
            }

            if (ctx.QueryInt("season.index") is { } seasonIndex)
            {
                if (plexType == 4)
                {
                    q.ParentIndexNumber = seasonIndex;
                }
                else
                {
                    q.IndexNumber = seasonIndex;
                }
            }

            if ((ctx.Query("title") ?? ctx.Query("title=")) is { Length: > 0 } title)
            {
                q.SearchTerm = title;
            }

            if (ctx.Query("year") is { } year)
            {
                q.Years = year.Split(',').Select(y => int.TryParse(y, out var v) ? v : 0).Where(v => v > 0).ToArray();
            }

            results.AddRange(_libraryManager.GetItemList(q));
        }

        var distinct = results.DistinctBy(i => i.Id).ToList();
        var node = PlexNode.Container().Set("allowSync", false).Set("identifier", MetadataMapper.LibraryIdentifier)
            .Set("mediaTagPrefix", "/system/bundle/media/flags/").Set("mediaTagVersion", 1);
        node.AddRange(_mapper.Map(distinct.Skip(start).Take(size ?? int.MaxValue).ToList(), user, new MapOptions()));
        return ctx.Write(node.WithSize(distinct.Count, start));
    }

    private static (string Name, string Value)? ProviderId(string guid)
    {
        var parts = guid.Split("://", 2);
        if (parts.Length != 2 || parts[1].Length == 0)
        {
            return null;
        }

        var name = parts[0].ToLowerInvariant() switch
        {
            "imdb" => MetadataProvider.Imdb.ToString(),
            "tmdb" => MetadataProvider.Tmdb.ToString(),
            "tvdb" => MetadataProvider.Tvdb.ToString(),
            _ => null,
        };
        return name is null ? null : (name, parts[1].Split('?')[0]);
    }

    /// <summary>
    /// PMS's flat search: SearchResult rows with a score and either Metadata (media) or a
    /// tag Directory (people, searchTypes=people).
    /// </summary>
    private Task LibrarySearch(PlexContext ctx)
    {
        var user = ctx.User;
        var term = ctx.Query("query") ?? string.Empty;
        var limit = Math.Clamp(ctx.QueryInt("limit") ?? 50, 1, 500);
        var node = PlexNode.Container().Set("allowSync", false).Set("identifier", MetadataMapper.LibraryIdentifier);
        if (term.Length == 0)
        {
            return ctx.Write(node.Set("size", 0));
        }

        var types = (ctx.Query("searchTypes") ?? "movies,tv,music").ToLowerInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        var kinds = new List<BaseItemKind>();
        if (types.Contains("movies") || types.Contains("movie"))
        {
            kinds.Add(BaseItemKind.Movie);
        }

        if (types.Contains("tv") || types.Contains("show"))
        {
            kinds.AddRange([BaseItemKind.Series, BaseItemKind.Episode]);
        }

        if (types.Contains("music"))
        {
            kinds.AddRange([BaseItemKind.MusicArtist, BaseItemKind.MusicAlbum, BaseItemKind.Audio]);
        }

        if (types.Contains("collections") || ctx.QueryBool("includeCollections"))
        {
            kinds.Add(BaseItemKind.BoxSet);
        }

        if (kinds.Count > 0)
        {
            var items = SearchItems(user, term, limit, kinds.ToArray());
            var mapped = _mapper.Map(items, user, new MapOptions { IncludeMedia = false });
            for (var i = 0; i < mapped.Count; i++)
            {
                node.Add(new PlexNode("SearchResult") { JsonSingleChildren = true }.Set("score", Math.Round(1.0 - (i / (double)Math.Max(mapped.Count, 1) * 0.5), 3)).Add(mapped[i]));
            }
        }

        if (types.Contains("people"))
        {
            var people = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = [BaseItemKind.Person],
                SearchTerm = term,
                Limit = limit,
                DtoOptions = new DtoOptions(false),
            });
            foreach (var person in people)
            {
                node.Add(new PlexNode("SearchResult") { JsonSingleChildren = true }.Set("score", 0.9).Add(PersonDirectory(person)));
            }
        }

        return ctx.Write(node.WithSize());
    }

    private PlexNode PersonDirectory(BaseItem person)
    {
        var id = _mapper.RatingKey(person);
        return new PlexNode("Directory")
            .Set("id", id)
            .Set("key", $"/library/people/{id}/media")
            .Set("type", "tag")
            .Set("tagType", 6)
            .Set("tag", person.Name)
            .Set("title", person.Name)
            .Set("thumb", person.HasImage(ImageType.Primary) ? $"/library/metadata/{id}/thumb/{MetadataMapper.Unix(person.DateModified)}" : null);
    }

    private BaseItem? FindPerson(PlexContext ctx) =>
        _ids.ResolveGuid(IdMap.Item, ctx.Route("id")) is { } id && _libraryManager.GetItemById(id) is Person person ? person : null;

    private Task Person(PlexContext ctx)
    {
        var person = FindPerson(ctx);
        return person is null ? ctx.Status(404) : ctx.Write(PlexNode.Container().Add(PersonDirectory(person)).WithSize());
    }

    private Task PersonMedia(PlexContext ctx)
    {
        var person = FindPerson(ctx);
        if (person is null)
        {
            return ctx.Status(404);
        }

        var (start, size) = ctx.Paging();
        var result = _libraryManager.GetItemsResult(new InternalItemsQuery(ctx.User)
        {
            PersonIds = [person.Id],
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode],
            Recursive = true,
            AncestorIds = SectionIds(ctx.User),
            OrderBy = [(ItemSortBy.PremiereDate, SortOrder.Descending), (ItemSortBy.SortName, SortOrder.Ascending)],
            StartIndex = start,
            Limit = size,
            EnableTotalRecordCount = true,
            DtoOptions = new DtoOptions(true),
        });
        var node = PlexNode.Container().Set("allowSync", false).Set("title1", person.Name);
        node.AddRange(_mapper.Map(result.Items, ctx.User, new MapOptions()));
        return ctx.Write(node.WithSize(result.TotalRecordCount, start));
    }

    private IReadOnlyList<BaseItem> SimilarItems(User user, BaseItem item, int count)
    {
        BaseItemKind[] kinds = item switch
        {
            Series => [BaseItemKind.Series],
            MusicArtist => [BaseItemKind.MusicArtist],
            MusicAlbum => [BaseItemKind.MusicAlbum],
            Episode or Season => [BaseItemKind.Series],
            _ => [BaseItemKind.Movie],
        };
        var genres = (item is Episode e ? e.Series?.Genres : item is Season s ? s.Series?.Genres : item.Genres) ?? [];
        if (genres.Length == 0)
        {
            return [];
        }

        var exclude = item switch { Episode ep when ep.Series is { } series => series.Id, Season se when se.Series is { } series => series.Id, _ => item.Id };
        return _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = kinds,
            Genres = genres,
            ExcludeItemIds = [exclude],
            Recursive = true,
            AncestorIds = SectionIds(user),
            OrderBy = [(ItemSortBy.Random, SortOrder.Ascending)],
            Limit = count,
            DtoOptions = new DtoOptions(true),
        });
    }

    private Task Similar(PlexContext ctx)
    {
        var item = FindItem(ctx, ctx.Route("id"));
        if (item is null)
        {
            return ctx.Status(404);
        }

        var node = PlexNode.Container().Set("allowSync", false);
        node.AddRange(_mapper.Map(SimilarItems(ctx.User, item, ctx.QueryInt("count") ?? 20), ctx.User, new MapOptions()));
        return ctx.Write(node.WithSize());
    }

    /// <summary>"More like this": items sharing genres, as one related hub.</summary>
    private Task RelatedHubs(PlexContext ctx)
    {
        var item = FindItem(ctx, ctx.Route("id"));
        if (item is null)
        {
            return ctx.Status(404);
        }

        var similar = SimilarItems(ctx.User, item, ctx.QueryInt("count") ?? 10);
        var node = PlexNode.Container().Set("allowSync", false).Set("identifier", MetadataMapper.LibraryIdentifier);
        if (similar.Count > 0)
        {
            var type = MetadataMapper.PlexType(similar[0]);
            var rk = _mapper.RatingKey(item);
            node.Add(Hub($"{type}.similar", "More Like This", $"/library/metadata/{rk}/similar", type,
                _mapper.Map(similar, ctx.User, new MapOptions()), false, $"hub.{type}.similar"));
        }

        return ctx.Write(node.WithSize());
    }

    /// <summary>The alphabet bar: how many top-level items start with each character.</summary>
    private Task FirstCharacter(PlexContext ctx)
    {
        var section = FindSection(ctx, ctx.Route("section"));
        if (section is null)
        {
            return ctx.Status(404);
        }

        IEnumerable<string> names;
        if (Audiobooks.IsBooks(section))
        {
            var index = _audiobooks.For(section);
            names = ctx.QueryInt("type") == 9 ? index.Books.Select(b => b.Title) : index.Authors.Select(a => a.Title);
        }
        else
        {
            var query = BuildSectionQuery(ctx, section);
            query.StartIndex = null;
            query.Limit = null;
            query.DtoOptions = new DtoOptions(false);
            names = section.GetItems(query).Items.Select(i => i.SortName ?? i.Name ?? string.Empty);
        }

        var counts = names
            .Select(n => n.TrimStart())
            .Where(n => n.Length > 0)
            .GroupBy(n => char.IsLetter(n[0]) ? char.ToUpperInvariant(n[0]).ToString() : "#")
            .OrderBy(g => g.Key == "#" ? string.Empty : g.Key, StringComparer.Ordinal);

        var node = PlexNode.Container().Set("allowSync", false).Set("title1", section.Name);
        foreach (var group in counts)
        {
            node.Add(new PlexNode("Directory").Set("key", group.Key).Set("title", group.Key).Set("size", group.Count()));
        }

        return ctx.Write(node.WithSize());
    }

    /// <summary>Genres as browsable categories (Plezy's genre row), keyed by genre filter.</summary>
    private Task Categories(PlexContext ctx)
    {
        var section = FindSection(ctx, ctx.Route("section"));
        if (section is null)
        {
            return ctx.Status(404);
        }

        var sectionKey = _mapper.SectionKey(section);
        var names = section.GetItems(new InternalItemsQuery(ctx.User)
        {
            Parent = section,
            Recursive = true,
            IncludeItemTypes = MetadataMapper.DefaultKindsForSection(section.CollectionType),
            DtoOptions = new DtoOptions(false),
        }).Items.SelectMany(i => i.Genres).GroupBy(g => g, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.Key);

        var node = PlexNode.Container().Set("allowSync", false).Set("title1", section.Name);
        foreach (var name in names)
        {
            var genre = section.CollectionType == CollectionType.music ? (BaseItem?)_libraryManager.GetMusicGenre(name) : _libraryManager.GetGenre(name);
            if (genre is null)
            {
                continue;
            }

            var key = _mapper.RatingKey(genre);
            node.Add(new PlexNode("Directory")
                .Set("key", $"/library/sections/{sectionKey}/all?genre={key}")
                .Set("title", name)
                .Set("type", "genre"));
        }

        return ctx.Write(node.WithSize());
    }

    /// <summary>Tag value lists for filters that aren't Jellyfin items (content rating, studio, decade).</summary>
    private Task SectionValues(PlexContext ctx, string field)
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
            IncludeItemTypes = MetadataMapper.KindsForPlexType(ctx.QueryInt("type")) ?? MetadataMapper.DefaultKindsForSection(section.CollectionType),
            DtoOptions = new DtoOptions(false),
        }).Items;

        IEnumerable<(string Key, string Title)> values = field switch
        {
            "contentRating" => items.Select(i => i.OfficialRating).OfType<string>().Where(r => r.Length > 0).Distinct().Order().Select(r => (r, r)),
            "label" => items.SelectMany(i => i.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Select(t => (t, t)),
            "studio" => items.SelectMany(i => i.Studios).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Select(st => (st, st)),
            _ => items.Select(i => i.ProductionYear).OfType<int>().Select(y => y / 10 * 10).Distinct().OrderDescending()
                .Select(d => (d.ToString(CultureInfo.InvariantCulture), d.ToString(CultureInfo.InvariantCulture) + "s")),
        };

        var sectionKey = _mapper.SectionKey(section);
        var node = PlexNode.Container().Set("allowSync", false).Set("title1", section.Name);
        foreach (var (key, title) in values)
        {
            node.Add(new PlexNode("Directory").Set("key", key).Set("title", title).Set("fastKey", $"/library/sections/{sectionKey}/all?{field}={Uri.EscapeDataString(key)}"));
        }

        return ctx.Write(node.WithSize());
    }

    /// <summary>
    /// Folder view. Jellyfin resolves most folders into items (a movie folder is the movie), so
    /// plain folders become browsable Directories and everything else is listed as itself.
    /// </summary>
    private Task Folder(PlexContext ctx)
    {
        var section = FindSection(ctx, ctx.Route("section"));
        if (section is null)
        {
            return ctx.Status(404);
        }

        var sectionKey = _mapper.SectionKey(section);
        IEnumerable<BaseItem> children;
        if (ctx.Query("parent") is { Length: > 0 } parentKey)
        {
            if (FindItem(ctx, parentKey) is not Folder parent)
            {
                return ctx.Status(404);
            }

            children = parent.GetChildren(ctx.User, true);
        }
        else
        {
            var roots = section.PhysicalLocations.Select(path => _libraryManager.FindByPath(path, true)).OfType<Folder>().ToList();
            children = roots.Count == 1 ? roots[0].GetChildren(ctx.User, true) : roots;
        }

        var node = PlexNode.Container().Set("allowSync", false).Set("librarySectionID", sectionKey).Set("title1", section.Name);
        foreach (var child in children.OrderBy(c => c.SortName, StringComparer.OrdinalIgnoreCase))
        {
            if (child.GetType() == typeof(Folder) || child is CollectionFolder)
            {
                var rk = _mapper.RatingKey(child);
                node.Add(new PlexNode("Directory")
                    .Set("key", $"/library/sections/{sectionKey}/folder?parent={rk}")
                    .Set("title", child.Name));
            }
            else
            {
                node.Add(_mapper.Map(child, ctx.User, new MapOptions { Section = section }));
            }
        }

        return ctx.Write(node.WithSize());
    }
}
