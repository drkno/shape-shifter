using System.Collections.Concurrent;
using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.ShapeShifter.Data;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.ShapeShifter.Library;

/// <summary>
/// Presents a Jellyfin "books" library the way PMS presents an audiobook library: a music
/// section of authors (artists), books (albums) and chapters (tracks). Jellyfin keeps no author or
/// book records, so they're derived like PMS's music scanner does: the author from the
/// album-artist tag, the book from the album tag, falling back to the folder layout
/// (Author/Book/chapter or Author/book-file). Chapters are the real Jellyfin AudioBook items, so
/// playback and watch state work unchanged; authors and books get stable ids of their own.
/// Only audio is exposed (PDF/ePub books have no Plex equivalent).
/// </summary>
public sealed class Audiobooks
{
    public const string AuthorPrefix = "bookauthor:";
    public const string BookPrefix = "book:";
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    private readonly ILibraryManager _libraryManager;
    private readonly IdMap _ids;
    private readonly ConcurrentDictionary<Guid, (DateTime Built, BookIndex Index)> _cache = new();

    public Audiobooks(ILibraryManager libraryManager, IdMap ids)
    {
        _libraryManager = libraryManager;
        _ids = ids;
    }

    public static bool IsBooks(CollectionFolder? section) => section?.CollectionType == CollectionType.books;

    public sealed class Author(long key, string title)
    {
        public long Key { get; } = key;

        public string Title { get; } = title;

        public List<Book> Books { get; } = [];

        public IEnumerable<BaseItem> Chapters => Books.SelectMany(b => b.Chapters);
    }

    public sealed class Book(long key, string title, Author author)
    {
        public long Key { get; } = key;

        public string Title { get; } = title;

        public Author Author { get; } = author;

        public List<BaseItem> Chapters { get; } = [];

        public int? Year => Chapters.Select(c => c.ProductionYear).FirstOrDefault(y => y is not null);

        public DateTime Added => Chapters.Min(c => c.DateCreated);
    }

    public sealed class BookIndex(CollectionFolder section)
    {
        public CollectionFolder Section { get; } = section;

        public List<Author> Authors { get; } = [];

        public Dictionary<long, Author> AuthorsByKey { get; } = [];

        public Dictionary<long, Book> BooksByKey { get; } = [];

        public Dictionary<Guid, (Book Book, int Index)> ChapterPlacement { get; } = [];

        public IEnumerable<Book> Books => Authors.SelectMany(a => a.Books);
    }

    /// <summary>The section's authors and books, rebuilt at most every few minutes.</summary>
    public BookIndex For(CollectionFolder section)
    {
        if (_cache.TryGetValue(section.Id, out var cached) && DateTime.UtcNow - cached.Built < CacheFor)
        {
            return cached.Index;
        }

        var index = Build(section);
        _cache[section.Id] = (DateTime.UtcNow, index);
        return index;
    }

    /// <summary>An author or book key, if <paramref name="ratingKey"/> is one.</summary>
    public (BookIndex Index, Author? Author, Book? Book)? Resolve(string? ratingKey)
    {
        if (!long.TryParse(ratingKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var key)
            || _ids.Resolve(IdMap.Item, key) is not { } mapped
            || !(mapped.StartsWith(AuthorPrefix, StringComparison.Ordinal) || mapped.StartsWith(BookPrefix, StringComparison.Ordinal)))
        {
            return null;
        }

        var sectionId = mapped[(mapped.IndexOf(':') + 1)..].Split(':')[0];
        if (!Guid.TryParse(sectionId, out var id) || _libraryManager.GetItemById(id) is not CollectionFolder section)
        {
            return null;
        }

        var index = For(section);
        return index.AuthorsByKey.TryGetValue(key, out var author) ? (index, author, null)
            : index.BooksByKey.TryGetValue(key, out var book) ? (index, null, book)
            : null;
    }

    /// <summary>Where a chapter sits (its book and 1-based position), if it's in a books library.</summary>
    public (Book Book, int Index)? Locate(BaseItem chapter)
    {
        if (chapter is not MediaBrowser.Controller.Entities.AudioBook
            || _libraryManager.GetCollectionFolders(chapter).OfType<CollectionFolder>().FirstOrDefault(s => IsBooks(s)) is not { } section)
        {
            return null;
        }

        return For(section).ChapterPlacement.TryGetValue(chapter.Id, out var placement) ? placement : null;
    }

    private BookIndex Build(CollectionFolder section)
    {
        var index = new BookIndex(section);
        // Through the library itself: a library view's own id isn't an ancestor of its files.
        var chapters = section.GetItems(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.AudioBook],
            Recursive = true,
            IsVirtualItem = false,
            DtoOptions = new DtoOptions(true),
        }).Items;
        var roots = section.PhysicalLocations.Select(p => p.TrimEnd('/') + "/").ToList();
        var authors = new Dictionary<string, Author>(StringComparer.OrdinalIgnoreCase);
        var books = new Dictionary<string, Book>(StringComparer.OrdinalIgnoreCase);
        var sectionKey = section.Id.ToString("N", CultureInfo.InvariantCulture);

        foreach (var chapter in chapters)
        {
            var segments = RelativeSegments(chapter.Path, roots);
            var audio = chapter as MediaBrowser.Controller.Entities.Audio.Audio;
            var authorName = FirstNonEmpty(audio?.AlbumArtists.FirstOrDefault(), audio?.Artists.FirstOrDefault(), segments.Length > 1 ? segments[0] : null) ?? "Unknown Author";
            var bookName = FirstNonEmpty(audio?.Album, segments.Length > 2 ? segments[1] : null, Path.GetFileNameWithoutExtension(chapter.Path)) ?? chapter.Name;

            if (!authors.TryGetValue(authorName, out var author))
            {
                author = new Author(_ids.GetOrAdd(IdMap.Item, $"{AuthorPrefix}{sectionKey}:{authorName.ToLowerInvariant()}"), authorName);
                authors[authorName] = author;
                index.Authors.Add(author);
                index.AuthorsByKey[author.Key] = author;
            }

            var bookKey = authorName + "\u001f" + bookName;
            if (!books.TryGetValue(bookKey, out var book))
            {
                book = new Book(_ids.GetOrAdd(IdMap.Item, $"{BookPrefix}{sectionKey}:{bookKey.ToLowerInvariant()}"), bookName, author);
                books[bookKey] = book;
                author.Books.Add(book);
                index.BooksByKey[book.Key] = book;
            }

            book.Chapters.Add(chapter);
        }

        index.Authors.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
        foreach (var author in index.Authors)
        {
            author.Books.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
            foreach (var book in author.Books)
            {
                book.Chapters.Sort((a, b) =>
                    (a.ParentIndexNumber ?? 0).CompareTo(b.ParentIndexNumber ?? 0) is var disc and not 0 ? disc
                    : (a.IndexNumber ?? 0).CompareTo(b.IndexNumber ?? 0) is var track and not 0 ? track
                    : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
                for (var i = 0; i < book.Chapters.Count; i++)
                {
                    index.ChapterPlacement[book.Chapters[i].Id] = (book, i + 1);
                }
            }
        }

        return index;
    }

    private static string[] RelativeSegments(string? path, IReadOnlyList<string> roots)
    {
        if (string.IsNullOrEmpty(path))
        {
            return [];
        }

        var root = roots.FirstOrDefault(r => path.StartsWith(r, StringComparison.Ordinal));
        return (root is null ? Path.GetFileName(path) : path[root.Length..]).Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
