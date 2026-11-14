using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ShapeShifter.Data;
using Jellyfin.Plugin.ShapeShifter.Plex;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Jellyfin.Plugin.ShapeShifter.Library;

namespace Jellyfin.Plugin.ShapeShifter.Api;

/// <summary>Options controlling how much detail is emitted for an item.</summary>
public sealed record MapOptions
{
    /// <summary>Gets a value indicating whether streams, people and extended fields are included (single item views).</summary>
    public bool Detailed { get; init; }

    /// <summary>Gets a value indicating whether Media/Part elements are included for playable items.</summary>
    public bool IncludeMedia { get; init; } = true;

    /// <summary>Gets the library section the items are known to belong to, avoiding a lookup per item.</summary>
    public CollectionFolder? Section { get; init; }
}

/// <summary>Translates Jellyfin items into PMS-shaped metadata nodes.</summary>
public sealed class MetadataMapper
{
    public const string LibraryIdentifier = "com.plexapp.plugins.library";

    private readonly IdMap _ids;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IMediaSourceManager _mediaSourceManager;

    private readonly Audiobooks _audiobooks;

    public MetadataMapper(IdMap ids, ILibraryManager libraryManager, IUserDataManager userDataManager, IMediaSourceManager mediaSourceManager, Audiobooks audiobooks)
    {
        _audiobooks = audiobooks;
        _ids = ids;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _mediaSourceManager = mediaSourceManager;
    }

    public long RatingKey(BaseItem item) => _ids.GetOrAdd(IdMap.Item, item.Id);

    public long SectionKey(BaseItem section) => _ids.GetOrAdd(IdMap.Section, section.Id);

    public static string PlexType(BaseItem item) => item switch
    {
        Episode => "episode",
        Series => "show",
        Season => "season",
        MusicArtist => "artist",
        MusicAlbum => "album",
        Audio => "track",
        Photo => "photo",
        PhotoAlbum => "photo",
        BoxSet => "collection",
        Playlist => "playlist",
        _ => "movie",
    };

    /// <summary>The PMS agent and scanner a library of this Plex type would have.</summary>
    public static (string Agent, string Scanner) AgentAndScanner(string plexSectionType) => plexSectionType switch
    {
        "show" => ("tv.plex.agents.series", "Plex TV Series"),
        "artist" => ("tv.plex.agents.music", "Plex Music"),
        "photo" => ("com.plexapp.agents.none", "Plex Photo Scanner"),
        _ => ("tv.plex.agents.movie", "Plex Movie"),
    };

    public static string SectionType(CollectionType? type) => type switch
    {
        CollectionType.tvshows => "show",
        CollectionType.music or CollectionType.books => "artist",
        CollectionType.photos => "photo",
        _ => "movie",
    };

    /// <summary>Plex numeric metadata types used by ?type= filters.</summary>
    public static BaseItemKind[]? KindsForPlexType(int? plexType) => plexType switch
    {
        1 => [BaseItemKind.Movie, BaseItemKind.Video, BaseItemKind.MusicVideo],
        2 => [BaseItemKind.Series],
        3 => [BaseItemKind.Season],
        4 => [BaseItemKind.Episode],
        8 => [BaseItemKind.MusicArtist],
        9 => [BaseItemKind.MusicAlbum],
        10 => [BaseItemKind.Audio],
        13 => [BaseItemKind.Photo],
        14 => [BaseItemKind.PhotoAlbum],
        18 => [BaseItemKind.BoxSet],
        _ => null,
    };

    public static BaseItemKind[] DefaultKindsForSection(CollectionType? type) => type switch
    {
        CollectionType.tvshows => [BaseItemKind.Series],
        CollectionType.music => [BaseItemKind.MusicArtist],
        CollectionType.books => [BaseItemKind.AudioBook],
        CollectionType.photos => [BaseItemKind.Photo, BaseItemKind.PhotoAlbum],
        _ => [BaseItemKind.Movie, BaseItemKind.Video, BaseItemKind.MusicVideo],
    };

    public static bool IsContainer(BaseItem item) => item is Series or Season or MusicArtist or MusicAlbum or PhotoAlbum or BoxSet or Playlist;

    public static long Unix(DateTime? date) =>
        date is { } d && d > DateTime.UnixEpoch ? new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)).ToUnixTimeSeconds() : 0;

    public static long Ms(long? ticks) => ticks is { } t ? t / TimeSpan.TicksPerMillisecond : 0;

    public List<PlexNode> Map(IReadOnlyList<BaseItem> items, User user, MapOptions options)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var userData = _userDataManager.GetUserDataBatch(items, user);
        var folderIds = items.Where(i => i is Series or Season or MusicAlbum or MusicArtist or BoxSet).Select(i => i.Id).ToList();
        var playedCounts = folderIds.Count > 0 ? _libraryManager.GetPlayedAndTotalCountBatch(folderIds, user) : null;
        var childCounts = folderIds.Count > 0 ? _libraryManager.GetChildCountBatch(folderIds, user) : null;

        var result = new List<PlexNode>(items.Count);
        foreach (var item in items)
        {
            userData.TryGetValue(item.Id, out var data);
            var node = MapOne(item, user, options, data);
            if (playedCounts is not null && playedCounts.TryGetValue(item.Id, out var counts))
            {
                node.Set("leafCount", counts.Total);
                node.Set("viewedLeafCount", counts.Played);
            }

            if (childCounts is not null && childCounts.TryGetValue(item.Id, out var childCount))
            {
                node.Set("childCount", childCount);
            }

            result.Add(node);
        }

        return result;
    }

    public PlexNode Map(BaseItem item, User user, MapOptions options) => Map([item], user, options)[0];

    private PlexNode MapOne(BaseItem item, User user, MapOptions options, UserItemData? data)
    {
        var rk = RatingKey(item);
        var type = PlexType(item);
        var element = item switch
        {
            Audio => "Track",
            Photo => "Photo",
            Playlist => "Playlist",
            _ when IsContainer(item) => "Directory",
            _ => "Video",
        };

        var node = PlexNode.Metadata(element)
            .Set("ratingKey", rk.ToString(CultureInfo.InvariantCulture))
            .Set("key", ItemKey(item, rk))
            .Set("guid", Guid(item))
            .Set("type", type)
            .Set("title", item.Name)
            .Set("titleSort", item.SortName != item.Name ? item.SortName : null)
            .Set("summary", item.Overview ?? string.Empty)
            .Set("index", item.IndexNumber)
            .Set("year", item.ProductionYear)
            .Set("originallyAvailableAt", item.PremiereDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Set("contentRating", item.OfficialRating)
            .Set("studio", item.Studios.FirstOrDefault())
            .Set("tagline", item.Tagline)
            .Set("audienceRating", item.CommunityRating is { } cr ? Math.Round(cr, 1) : null)
            .Set("rating", item.CriticRating is { } crit ? Math.Round(crit / 10.0, 1) : null)
            .Set("duration", item.RunTimeTicks is > 0 ? Ms(item.RunTimeTicks) : null)
            .Set("addedAt", Unix(item.DateCreated))
            .Set("updatedAt", Unix(item.DateModified));

        var section = options.Section ?? _libraryManager.GetCollectionFolders(item).FirstOrDefault();
        if (section is not null)
        {
            node.Set("librarySectionID", SectionKey(section))
                .Set("librarySectionTitle", section.Name)
                .Set("librarySectionUUID", section.Id.ToString("D"))
                .Set("librarySectionKey", $"/library/sections/{SectionKey(section)}");
        }

        SetImages(node, item, rk);
        SetHierarchy(node, item);
        SetUserData(node, item, data);
        SetListAttributes(node, item, user, rk);
        SetGuids(node, item);

        if (options.Detailed)
        {
            foreach (var genre in item.Genres)
            {
                node.AddTag("Genre", genre);
            }

            foreach (var person in _libraryManager.GetPeople(item).Take(50))
            {
                var tagName = person.Type switch
                {
                    PersonKind.Director => "Director",
                    PersonKind.Writer => "Writer",
                    PersonKind.Producer => "Producer",
                    PersonKind.Actor or PersonKind.GuestStar => "Role",
                    _ => null,
                };
                if (tagName is not null)
                {
                    node.AddTag(tagName, person.Name, n => SetPersonTag(n, person.Name, person.Role, tagName));
                }
            }
        }
        else
        {
            foreach (var genre in item.Genres.Take(2))
            {
                node.AddTag("Genre", genre);
            }
        }

        if (options.IncludeMedia && item is Video or Audio)
        {
            AddMedia(node, item, user, options.Detailed, data);
        }

        return node;
    }

    private static string Guid(BaseItem item)
    {
        // Real PMS uses plex:// agent guids; clients only compare them for equality.
        return $"{LibraryIdentifier}://{PlexType(item)}/{item.Id:N}";
    }

    private void SetImages(PlexNode node, BaseItem item, long rk)
    {
        var ts = Unix(item.DateModified);
        if (item.HasImage(ImageType.Primary))
        {
            node.Set("thumb", $"/library/metadata/{rk}/thumb/{ts}");
        }

        if (item.HasImage(ImageType.Backdrop))
        {
            node.Set("art", $"/library/metadata/{rk}/art/{ts}");
        }

        if (item.HasImage(ImageType.Banner))
        {
            node.Set("banner", $"/library/metadata/{rk}/banner/{ts}");
        }
    }

    private void SetParent(PlexNode node, string prefix, BaseItem? parent)
    {
        if (parent is null)
        {
            return;
        }

        var rk = RatingKey(parent);
        var ts = Unix(parent.DateModified);
        node.Set($"{prefix}RatingKey", rk.ToString(CultureInfo.InvariantCulture))
            .Set($"{prefix}Key", $"/library/metadata/{rk}")
            .Set($"{prefix}Title", parent.Name)
            .Set($"{prefix}Guid", Guid(parent));
        if (parent.HasImage(ImageType.Primary))
        {
            node.Set($"{prefix}Thumb", $"/library/metadata/{rk}/thumb/{ts}");
        }

        if (parent.HasImage(ImageType.Backdrop))
        {
            node.Set($"{prefix}Art", $"/library/metadata/{rk}/art/{ts}");
        }
    }

    private void SetHierarchy(PlexNode node, BaseItem item)
    {
        switch (item)
        {
            case Episode episode:
                SetParent(node, "grandparent", episode.Series);
                SetParent(node, "parent", episode.Season);
                node.Set("parentIndex", episode.ParentIndexNumber ?? episode.Season?.IndexNumber);
                if (episode.Series is { } series && !series.HasImage(ImageType.Backdrop) && node.Get("art") is null)
                {
                    node.Set("art", node.Get("grandparentArt"));
                }

                break;
            case Season season:
                SetParent(node, "parent", season.Series);
                break;
            case MusicAlbum album:
                SetParent(node, "parent", ArtistOf(album.AlbumArtists));
                break;
            case AudioBook chapter when _audiobooks.Locate(chapter) is { } placement:
                // Audiobook chapters belong to derived author/book entries (see Audiobooks).
                var book = placement.Book;
                node.Set("parentRatingKey", book.Key.ToString(CultureInfo.InvariantCulture))
                    .Set("parentKey", $"/library/metadata/{book.Key}")
                    .Set("parentTitle", book.Title)
                    .Set("grandparentRatingKey", book.Author.Key.ToString(CultureInfo.InvariantCulture))
                    .Set("grandparentKey", $"/library/metadata/{book.Author.Key}")
                    .Set("grandparentTitle", book.Author.Title)
                    .Set("parentIndex", 1)
                    .Set("index", placement.Index)
                    .Set("parentThumb", node.Get("thumb"))
                    .Set("grandparentThumb", node.Get("thumb"));
                break;
            case Audio track:
                SetParent(node, "grandparent", ArtistOf(track.AlbumArtists));
                SetParent(node, "parent", track.AlbumEntity);
                node.Set("parentIndex", track.ParentIndexNumber ?? 1);
                node.Set("originalTitle", track.Artists.FirstOrDefault() is { } a && !track.AlbumArtists.Contains(a) ? a : null);
                break;
        }
    }

    private MusicArtist? ArtistOf(IReadOnlyList<string> names) =>
        names.Count > 0 ? _libraryManager.GetArtist(names[0]) : null;

    private static void SetUserData(PlexNode node, BaseItem item, UserItemData? data)
    {
        if (data is null)
        {
            return;
        }

        // Plex treats any viewCount as "watched". Jellyfin bumps PlayCount when playback
        // starts, so only Played (set at completion or by mark-as-watched) counts.
        if (data.Played)
        {
            node.Set("viewCount", Math.Max(1, data.PlayCount));
        }

        if (data.PlaybackPositionTicks > 0)
        {
            node.Set("viewOffset", Ms(data.PlaybackPositionTicks));
        }

        if (data.LastPlayedDate is { } last)
        {
            node.Set("lastViewedAt", Unix(last));
        }

        if (data.Rating is { } rating)
        {
            node.Set("userRating", Math.Round(rating, 1));
        }
        else if (data.IsFavorite)
        {
            node.Set("userRating", 10);
        }
    }

    private void AddMedia(PlexNode node, BaseItem item, User user, bool detailed, UserItemData? data)
    {
        var sources = _mediaSourceManager.GetStaticMediaSources(item, false, user);
        foreach (var source in sources)
        {
            node.Add(MapMedia(item, source, detailed, data?.AudioStreamIndex, data?.SubtitleStreamIndex));
        }
    }

    /// <param name="audioIndex">Chosen audio stream (Jellyfin index), or null for the default.</param>
    /// <param name="subtitleIndex">Chosen subtitle stream, -1 for none, or null for the default (none unless forced).</param>
    /// <summary>Where a client fetches an item's contents: collections and playlists have their own endpoints in PMS.</summary>
    public static string ItemKey(BaseItem item, long rk) => item switch
    {
        BoxSet => $"/library/collections/{rk}/children",
        Playlist => $"/playlists/{rk}/items",
        _ when IsContainer(item) => $"/library/metadata/{rk}/children",
        _ => $"/library/metadata/{rk}",
    };

    /// <summary>External ids as PMS's Guid tags; clients match items across servers and plex.tv by these.</summary>
    private static void SetGuids(PlexNode node, BaseItem item)
    {
        foreach (var (provider, value) in item.ProviderIds)
        {
            var scheme = provider.ToLowerInvariant() switch
            {
                "imdb" => "imdb",
                "tmdb" => "tmdb",
                "tvdb" => "tvdb",
                _ => null,
            };
            if (scheme is not null && !string.IsNullOrEmpty(value))
            {
                node.Add(new PlexNode("Guid").Set("id", $"{scheme}://{value}"));
            }
        }
    }

    private void SetListAttributes(PlexNode node, BaseItem item, User user, long rk)
    {
        switch (item)
        {
            case Playlist playlist:
                var entries = playlist.GetLinkedChildren(user);
                node.Set("playlistType", playlist.MediaType switch { MediaType.Audio => "audio", MediaType.Photo => "photo", _ => "video" })
                    .Set("smart", false)
                    .Set("leafCount", entries.Count)
                    .Set("duration", Ms(entries.Sum(e => e.RunTimeTicks ?? 0)))
                    .Set("composite", $"/playlists/{rk}/composite/{Unix(item.DateModified)}");
                break;
            case BoxSet set:
                var children = set.GetLinkedChildren(user);
                node.Set("subtype", children.FirstOrDefault() is { } first ? PlexType(first) : "movie")
                    .Set("childCount", children.Count)
                    .Set("smart", false)
                    .Set("collectionMode", -1);
                break;
        }
    }

    /// <summary>
    /// Cast and crew tags carry the person's ratingKey as id, so /library/people/{id} and
    /// ?actor={id} filters work, plus their photo.
    /// </summary>
    private void SetPersonTag(PlexNode tag, string name, string? role, string tagName)
    {
        tag.Set("role", string.IsNullOrEmpty(role) ? null : role);
        if (_libraryManager.GetPerson(name) is { } person)
        {
            var id = RatingKey(person);
            tag.Set("id", id)
                .Set("filter", $"{(tagName == "Role" ? "actor" : tagName.ToLowerInvariant())}={id}")
                .Set("tagKey", null)
                .Set("thumb", person.HasImage(ImageType.Primary) ? $"/library/metadata/{id}/thumb/{Unix(person.DateModified)}" : null);
        }
    }

    public PlexNode MapMedia(BaseItem item, MediaSourceInfo source, bool detailed, int? audioIndex = null, int? subtitleIndex = null)
    {
        var partKey = $"{item.Id:N}|{source.Id}";
        var partId = _ids.GetOrAdd(IdMap.Part, partKey);
        var video = source.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        var audio = source.MediaStreams.Where(s => s.Type == MediaStreamType.Audio).OrderByDescending(s => s.IsDefault).FirstOrDefault();
        var container = NormaliseContainer(source.Container);
        var ext = Path.GetExtension(source.Path)?.TrimStart('.') is { Length: > 0 } e ? e : container ?? "bin";

        var media = new PlexNode("Media")
            .Set("id", partId)
            .Set("duration", Ms(source.RunTimeTicks ?? item.RunTimeTicks))
            .Set("bitrate", source.Bitrate is { } br ? br / 1000 : null)
            .Set("container", container)
            .Set("audioCodec", audio?.Codec)
            .Set("audioChannels", audio?.Channels)
            .Set("audioProfile", audio?.Profile?.ToLowerInvariant())
            .Set("optimizedForStreaming", 1);

        if (video is not null)
        {
            media.Set("videoCodec", video.Codec)
                .Set("width", video.Width)
                .Set("height", video.Height)
                .Set("aspectRatio", video.Width is > 0 && video.Height is > 0 ? Math.Round((double)video.Width.Value / video.Height.Value, 2) : null)
                .Set("videoResolution", Resolution(video.Width, video.Height))
                .Set("videoFrameRate", FrameRate(video.RealFrameRate ?? video.AverageFrameRate))
                .Set("videoProfile", video.Profile?.ToLowerInvariant());
        }

        var part = new PlexNode("Part")
            .Set("id", partId)
            .Set("key", $"/library/parts/{partId}/{Unix(item.DateModified)}/file.{ext}")
            .Set("duration", Ms(source.RunTimeTicks ?? item.RunTimeTicks))
            .Set("file", source.Path)
            .Set("size", source.Size)
            .Set("container", container)
            .Set("videoProfile", video?.Profile?.ToLowerInvariant());

        if (detailed)
        {
            // Plex clients read the playing streams from selected="1" (Plex Web's player settings
            // crash without a selected audio stream), so exactly one video and audio are marked.
            var streams = source.MediaStreams;
            var selectedAudio = streams.FirstOrDefault(x => x.Type == MediaStreamType.Audio && x.Index == audioIndex) ?? audio;
            var selectedSubtitle = subtitleIndex switch
            {
                -1 => null,
                { } i => streams.FirstOrDefault(x => x.Type == MediaStreamType.Subtitle && x.Index == i),
                null => streams.FirstOrDefault(x => x.Type == MediaStreamType.Subtitle && x.IsForced),
            };
            foreach (var stream in streams)
            {
                if (MapStream(item, source, stream) is { } s)
                {
                    if (stream == video || stream == selectedAudio || stream == selectedSubtitle)
                    {
                        s.Set("selected", true);
                    }

                    part.Add(s);
                }
            }
        }

        media.Add(part);
        return media;
    }

    private PlexNode? MapStream(BaseItem item, MediaSourceInfo source, MediaStream stream)
    {
        var streamType = stream.Type switch
        {
            MediaStreamType.Video => 1,
            MediaStreamType.Audio => 2,
            MediaStreamType.Subtitle => 3,
            _ => 0,
        };
        if (streamType == 0)
        {
            return null;
        }

        var id = _ids.GetOrAdd(IdMap.Stream, $"{item.Id:N}|{source.Id}|{stream.Index}");
        var node = new PlexNode("Stream")
            .Set("id", id)
            .Set("streamType", streamType)
            .Set("index", stream.Index)
            .Set("codec", stream.Codec?.ToLowerInvariant())
            .Set("default", stream.IsDefault ? true : null)
            .Set("forced", stream.IsForced ? true : null)
            .Set("bitrate", stream.BitRate is { } br ? br / 1000 : null)
            .Set("language", stream.Language is { } l ? CultureLanguage(l) : null)
            .Set("languageCode", stream.Language)
            .Set("title", stream.Title)
            .Set("displayTitle", stream.DisplayTitle)
            .Set("extendedDisplayTitle", stream.DisplayTitle);

        switch (stream.Type)
        {
            case MediaStreamType.Video:
                node.Set("width", stream.Width).Set("height", stream.Height)
                    .Set("frameRate", stream.RealFrameRate is { } fr ? Math.Round(fr, 3) : null)
                    .Set("profile", stream.Profile?.ToLowerInvariant())
                    .Set("level", stream.Level is { } lv ? (int)lv : null)
                    .Set("bitDepth", stream.BitDepth)
                    .Set("colorSpace", stream.ColorSpace);
                break;
            case MediaStreamType.Audio:
                node.Set("channels", stream.Channels)
                    .Set("samplingRate", stream.SampleRate)
                    .Set("audioChannelLayout", stream.ChannelLayout)
                    .Set("profile", stream.Profile?.ToLowerInvariant());
                break;
            case MediaStreamType.Subtitle:
                if (stream.IsExternal || stream.IsTextSubtitleStream)
                {
                    node.Set("key", $"/library/streams/{id}");
                }

                break;
        }

        return node;
    }

    private static string? NormaliseContainer(string? container) =>
        container?.Split(',')[0].ToLowerInvariant() switch
        {
            "matroska" or "webm" => "mkv",
            "mov" => "mov",
            null => null,
            var c => c,
        };

    private static string? Resolution(int? width, int? height)
    {
        if (width is null || height is null)
        {
            return null;
        }

        return (width, height) switch
        {
            ( >= 3200, _) or (_, >= 1800) => "4k",
            ( >= 1700, _) or (_, >= 1000) => "1080",
            ( >= 1100, _) or (_, >= 700) => "720",
            (_, >= 560) => "576",
            (_, >= 400) => "480",
            _ => "sd",
        };
    }

    private static string? FrameRate(float? fps) => fps switch
    {
        null => null,
        >= 23.5f and < 24.5f => "24p",
        >= 24.5f and < 26f => "PAL",
        >= 29f and < 31f => "NTSC",
        >= 49f and < 51f => "50p",
        >= 59f and < 61f => "60p",
        _ => Math.Round(fps.Value).ToString(CultureInfo.InvariantCulture) + "p",
    };

    private static string CultureLanguage(string code)
    {
        try
        {
            return CultureInfo.GetCultureInfo(code).EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }
}
