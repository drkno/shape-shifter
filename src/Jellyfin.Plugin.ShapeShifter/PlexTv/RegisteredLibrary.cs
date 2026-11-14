namespace Jellyfin.Plugin.ShapeShifter.PlexTv;

/// <summary>A library as plex.tv's server registry is told about it.</summary>
/// <param name="Key">Plex section key (what clients use in /library/sections/{key}).</param>
/// <param name="Type">Plex section type: movie, show, artist or photo.</param>
public sealed record RegisteredLibrary(long Key, string Type, string Title, string Uuid, long CreatedAt, long UpdatedAt, IReadOnlyList<string> Paths);
