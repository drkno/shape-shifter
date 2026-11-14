using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.ShapeShifter.Api;

/// <summary>
/// Parsed form of a Plex metadata edit. Plex sends edits as query parameters:
/// <c>title.value=X</c>, <c>title.locked=1</c>, <c>genre[0].tag.tag=Drama</c> (add),
/// <c>genre[].tag.tag-=Drama,Comedy</c> (remove), <c>genre.locked=1</c>.
/// </summary>
public sealed partial class MetadataEditRequest
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, bool> Locks { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, List<string>> TagAdds { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, List<string>> TagRemoves { get; } = new(StringComparer.Ordinal);

    public bool IsEmpty => Values.Count == 0 && Locks.Count == 0 && TagAdds.Count == 0 && TagRemoves.Count == 0;

    public static MetadataEditRequest Parse(IEnumerable<KeyValuePair<string, string?>> query)
    {
        var request = new MetadataEditRequest();
        var indexedAdds = new Dictionary<string, SortedDictionary<int, string>>(StringComparer.Ordinal);
        foreach (var (key, raw) in query)
        {
            var value = raw ?? string.Empty;
            if (TagParam().Match(key) is { Success: true } tag)
            {
                var field = tag.Groups["field"].Value;
                if (tag.Groups["remove"].Success)
                {
                    Add(request.TagRemoves, field, value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
                else if (value.Length > 0)
                {
                    if (!indexedAdds.TryGetValue(field, out var byIndex))
                    {
                        indexedAdds[field] = byIndex = [];
                    }

                    byIndex[tag.Groups["index"].Success ? int.Parse(tag.Groups["index"].Value, System.Globalization.CultureInfo.InvariantCulture) : byIndex.Count] = value;
                }

                continue;
            }

            if (key.EndsWith(".value", StringComparison.Ordinal))
            {
                request.Values[key[..^6]] = value;
            }
            else if (key.EndsWith(".locked", StringComparison.Ordinal))
            {
                request.Locks[key[..^7]] = value == "1";
            }
        }

        foreach (var (field, byIndex) in indexedAdds)
        {
            Add(request.TagAdds, field, byIndex.Values);
        }

        return request;
    }

    private static void Add(Dictionary<string, List<string>> target, string field, IEnumerable<string> values)
    {
        if (!target.TryGetValue(field, out var list))
        {
            target[field] = list = [];
        }

        list.AddRange(values);
    }

    [GeneratedRegex(@"^(?<field>[a-zA-Z]+)\[(?<index>\d+)?\]\.tag\.tag(?<remove>-)?$")]
    private static partial Regex TagParam();
}
