using System.Globalization;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ShapeShifter.Library;
using Jellyfin.Plugin.ShapeShifter.Plex;
using Jellyfin.Plugin.ShapeShifter.Server;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.ShapeShifter.Api;

/// <summary>
/// A Jellyfin books library served as PMS serves an audiobook library: a music section of
/// authors (artist), books (album) and chapters (track). See <see cref="Audiobooks"/>.
/// </summary>
public sealed partial class LibraryEndpoints
{
    private PlexNode AuthorNode(Audiobooks.BookIndex index, Audiobooks.Author author, User user)
    {
        var node = DerivedNode(index, author.Key, "artist", author.Title, author.Chapters.FirstOrDefault(), user)
            .Set("childCount", author.Books.Count)
            .Set("leafCount", author.Books.Sum(b => b.Chapters.Count))
            .Set("addedAt", MetadataMapper.Unix(author.Books.Min(b => b.Added)));
        return node;
    }

    private PlexNode BookNode(Audiobooks.BookIndex index, Audiobooks.Book book, User user)
    {
        var played = _userDataManager.GetUserDataBatch(book.Chapters, user).Values.Count(d => d.Played);
        return DerivedNode(index, book.Key, "album", book.Title, book.Chapters.FirstOrDefault(), user)
            .Set("parentRatingKey", book.Author.Key.ToString(CultureInfo.InvariantCulture))
            .Set("parentKey", $"/library/metadata/{book.Author.Key}")
            .Set("parentTitle", book.Author.Title)
            .Set("year", book.Year)
            .Set("leafCount", book.Chapters.Count)
            .Set("viewedLeafCount", played)
            .Set("duration", MetadataMapper.Ms(book.Chapters.Sum(c => c.RunTimeTicks ?? 0)))
            .Set("addedAt", MetadataMapper.Unix(book.Added));
    }

    /// <summary>A Directory for a derived author or book, with artwork borrowed from its first chapter.</summary>
    private PlexNode DerivedNode(Audiobooks.BookIndex index, long key, string type, string title, BaseItem? artworkFrom, User user)
    {
        var sectionKey = _mapper.SectionKey(index.Section);
        var node = new PlexNode("Directory", "Metadata")
            .Set("ratingKey", key.ToString(CultureInfo.InvariantCulture))
            .Set("key", $"/library/metadata/{key}/children")
            .Set("type", type)
            .Set("title", title)
            .Set("summary", string.Empty)
            .Set("librarySectionID", sectionKey)
            .Set("librarySectionTitle", index.Section.Name)
            .Set("librarySectionUUID", index.Section.Id.ToString("D"))
            .Set("librarySectionKey", $"/library/sections/{sectionKey}");
        if (artworkFrom is not null && artworkFrom.HasImage(MediaBrowser.Model.Entities.ImageType.Primary))
        {
            node.Set("thumb", $"/library/metadata/{_mapper.RatingKey(artworkFrom)}/thumb/{MetadataMapper.Unix(artworkFrom.DateModified)}");
        }

        return node;
    }

    /// <summary>Authors by default; books for type=9; chapters for type=10, as PMS's music sections list.</summary>
    private Task BooksSectionAll(PlexContext ctx, CollectionFolder section)
    {
        var index = _audiobooks.For(section);
        var user = ctx.User;
        var (start, size) = ctx.Paging();
        var title = ctx.Query("title");
        bool Matches(string t) => string.IsNullOrEmpty(title) || t.Contains(title, StringComparison.OrdinalIgnoreCase);
        var descending = ctx.Query("sort")?.Contains("desc", StringComparison.OrdinalIgnoreCase) == true;
        var byAdded = ctx.Query("sort")?.StartsWith("addedAt", StringComparison.Ordinal) == true;

        var container = PlexNode.Container()
            .Set("allowSync", false)
            .Set("librarySectionID", _mapper.SectionKey(section))
            .Set("librarySectionTitle", section.Name)
            .Set("librarySectionUUID", section.Id.ToString("D"))
            .Set("title1", section.Name);
        int total;
        switch (ctx.QueryInt("type"))
        {
            case 9:
                var books = index.Books.Where(b => Matches(b.Title));
                books = byAdded ? books.OrderBy(b => b.Added) : books.OrderBy(b => b.Title, StringComparer.OrdinalIgnoreCase);
                var bookList = (descending ? books.Reverse() : books).ToList();
                total = bookList.Count;
                container.Set("viewGroup", "album").AddRange(bookList.Skip(start).Take(size ?? int.MaxValue).Select(b => BookNode(index, b, user)));
                break;
            case 10:
                var chapters = index.Books.SelectMany(b => b.Chapters).Where(c => Matches(c.Name)).ToList();
                total = chapters.Count;
                container.Set("viewGroup", "track").AddRange(_mapper.Map(chapters.Skip(start).Take(size ?? int.MaxValue).ToList(), user, new MapOptions { Section = section }));
                break;
            default:
                var authors = index.Authors.Where(a => Matches(a.Title));
                authors = byAdded ? authors.OrderBy(a => a.Books.Min(b => b.Added)) : authors;
                var authorList = (descending ? authors.Reverse() : authors).ToList();
                total = authorList.Count;
                container.Set("viewGroup", "artist").AddRange(authorList.Skip(start).Take(size ?? int.MaxValue).Select(a => AuthorNode(index, a, user)));
                break;
        }

        return ctx.Write(container.WithSize(total, start));
    }

    /// <summary>The Books "Recommended" tab: books in progress, then recently added books.</summary>
    private Task BooksSectionHubs(PlexContext ctx, CollectionFolder section)
    {
        var index = _audiobooks.For(section);
        var user = ctx.User;
        var count = ctx.QueryInt("count") ?? DefaultHubSize;
        var id = _mapper.SectionKey(section);
        var container = PlexNode.Container().Set("allowSync", false).Set("librarySectionID", id).Set("librarySectionTitle", section.Name).Set("librarySectionUUID", section.Id.ToString("D"));

        // In progress: some listening done (a resume point or a finished chapter) but not all of it.
        var allChapters = index.Books.SelectMany(b => b.Chapters).ToList();
        var data = _userDataManager.GetUserDataBatch(allChapters, user);
        var inProgress = index.Books
            .Select(b => (Book: b, States: b.Chapters.Select(c => data.TryGetValue(c.Id, out var d) ? d : null).ToList()))
            .Where(x => x.States.Any(d => d is not null && (d.Played || d.PlaybackPositionTicks > 0)) && !x.States.All(d => d?.Played == true))
            .OrderByDescending(x => x.States.Max(d => d?.LastPlayedDate ?? DateTime.MinValue))
            .Take(count)
            .Select(x => BookNode(index, x.Book, user))
            .ToList();
        if (inProgress.Count > 0)
        {
            container.Add(Hub($"music.inprogress.{id}", "Continue Listening", $"/hubs/sections/{id}", "album", inProgress, false, "hub.music.inprogress"));
        }

        var recent = index.Books.OrderByDescending(b => b.Added).Take(count).Select(b => BookNode(index, b, user)).ToList();
        container.Add(Hub($"music.recent.added.{id}", "Recently Added", $"/library/sections/{id}/all?type=9&sort=addedAt:desc", "album", recent, index.BooksByKey.Count > count, "hub.music.recent.added"));
        return ctx.Write(container.WithSize());
    }

    private Task BooksMetadata(PlexContext ctx, IEnumerable<string> keys)
    {
        var container = PlexNode.Container().Set("allowSync", false).Set("identifier", MetadataMapper.LibraryIdentifier);
        foreach (var key in keys)
        {
            if (_audiobooks.Resolve(key) is { } derived)
            {
                container.Add(derived.Author is { } author ? AuthorNode(derived.Index, author, ctx.User) : BookNode(derived.Index, derived.Book!, ctx.User));
            }
            else if (FindItem(ctx, key) is { } item)
            {
                container.Add(_mapper.Map(item, ctx.User, new MapOptions { Detailed = true }));
            }
        }

        return container.Children.Count == 0 ? ctx.Status(404) : ctx.Write(container.WithSize());
    }

    /// <summary>An author's books (or, for allLeaves, all their chapters); a book's chapters.</summary>
    private Task BooksChildren(PlexContext ctx, Audiobooks.BookIndex index, Audiobooks.Author? author, Audiobooks.Book? book, bool leaves)
    {
        var (start, size) = ctx.Paging();
        var container = PlexNode.Container()
            .Set("allowSync", false)
            .Set("librarySectionID", _mapper.SectionKey(index.Section))
            .Set("librarySectionTitle", index.Section.Name)
            .Set("librarySectionUUID", index.Section.Id.ToString("D"));
        int total;
        if (author is not null && !leaves)
        {
            total = author.Books.Count;
            container.Set("key", author.Key.ToString(CultureInfo.InvariantCulture)).Set("title1", author.Title).Set("viewGroup", "album")
                .AddRange(author.Books.Skip(start).Take(size ?? int.MaxValue).Select(b => BookNode(index, b, ctx.User)));
        }
        else
        {
            var chapters = book?.Chapters ?? author!.Chapters.ToList();
            total = chapters.Count;
            container.Set("key", (book?.Key ?? author!.Key).ToString(CultureInfo.InvariantCulture))
                .Set("title1", book?.Author.Title ?? author!.Title)
                .Set("title2", book?.Title)
                .Set("parentTitle", book?.Title)
                .Set("viewGroup", "track")
                .AddRange(_mapper.Map(chapters.Skip(start).Take(size ?? int.MaxValue).ToList(), ctx.User, new MapOptions { Section = index.Section }));
        }

        return ctx.Write(container.WithSize(total, start));
    }
}
