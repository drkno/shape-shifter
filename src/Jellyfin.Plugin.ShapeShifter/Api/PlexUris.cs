using System.Text.RegularExpressions;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ShapeShifter.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.ShapeShifter.Api;

/// <summary>
/// Items named by a Plex item URI, as sent to playlists, collections and play queues:
/// server://{machineId}/com.plexapp.plugins.library/library/metadata/1,2,3 or
/// library://{uuid}/item/%2Flibrary%2Fmetadata%2F1 (or a bare /library/metadata/1,2 path).
/// </summary>
public static partial class PlexUris
{
    public static IReadOnlyList<BaseItem> Items(string? uri, User user, IdMap ids, ILibraryManager libraryManager)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return [];
        }

        var match = MetadataKeys().Match(Uri.UnescapeDataString(uri));
        if (!match.Success)
        {
            return [];
        }

        var items = new List<BaseItem>();
        foreach (var key in match.Groups["rks"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (ids.ResolveGuid(IdMap.Item, key) is { } id && libraryManager.GetItemById(id) is { } item && item.IsVisible(user))
            {
                items.Add(item);
            }
        }

        return items;
    }

    [GeneratedRegex(@"/library/metadata/(?<rks>\d+(?:,\d+)*)")]
    private static partial Regex MetadataKeys();
}
