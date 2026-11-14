namespace Jellyfin.Plugin.ShapeShifter.Plex;

/// <summary>
/// A loosely typed Plex response element. PMS responses are attribute-heavy XML trees
/// (MediaContainer → Video → Media → Part → Stream); the JSON form is the same tree with
/// children grouped into arrays. Keeping one generic node lets us mirror real PMS output
/// attribute-for-attribute without a class per element.
/// </summary>
public sealed class PlexNode
{
    public PlexNode(string name, string? jsonKey = null)
    {
        Name = name;
        JsonKey = jsonKey ?? name;
    }

    /// <summary>Gets the XML element name (Video, Directory, Track, ...).</summary>
    public string Name { get; }

    /// <summary>Gets the JSON array this node is placed in under its parent (Metadata, Directory, Media, ...).</summary>
    public string JsonKey { get; }

    public List<KeyValuePair<string, object>> Attributes { get; } = [];

    public List<PlexNode> Children { get; } = [];

    /// <summary>Gets or sets a value indicating whether a lone child of each kind is written as a JSON object rather than a one-element array (PMS does this for SearchResult).</summary>
    public bool JsonSingleChildren { get; set; }

    public static PlexNode Container() => new("MediaContainer");

    /// <summary>Metadata items: XML name depends on type, JSON always groups them as "Metadata".</summary>
    public static PlexNode Metadata(string elementName) => new(elementName, "Metadata");

    /// <summary>Sets an attribute. Null and empty-string values are skipped, matching PMS which omits unknown fields.</summary>
    public PlexNode Set(string key, object? value)
    {
        if (value is null || (value is string s && s.Length == 0))
        {
            return this;
        }

        var index = Attributes.FindIndex(a => a.Key == key);
        if (index >= 0)
        {
            Attributes[index] = new(key, value);
        }
        else
        {
            Attributes.Add(new(key, value));
        }

        return this;
    }

    public object? Get(string key) => Attributes.FirstOrDefault(a => a.Key == key).Value;

    public PlexNode Add(PlexNode child)
    {
        Children.Add(child);
        return this;
    }

    public PlexNode AddRange(IEnumerable<PlexNode> children)
    {
        Children.AddRange(children);
        return this;
    }

    /// <summary>Adds a tag child such as Genre/Director/Role (&lt;Genre tag="Drama"/&gt;).</summary>
    public PlexNode AddTag(string name, string tag, Action<PlexNode>? configure = null)
    {
        var node = new PlexNode(name).Set("tag", tag);
        configure?.Invoke(node);
        return Add(node);
    }

    /// <summary>Sets size (and optional paging attributes) on a container from its children.</summary>
    public PlexNode WithSize(int? totalSize = null, int? offset = null)
    {
        Set("size", Children.Count);
        if (totalSize is not null)
        {
            Set("totalSize", totalSize.Value);
            Set("offset", offset ?? 0);
        }

        return this;
    }
}
