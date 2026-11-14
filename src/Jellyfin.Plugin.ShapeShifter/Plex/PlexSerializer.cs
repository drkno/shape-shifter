using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace Jellyfin.Plugin.ShapeShifter.Plex;

public static class PlexSerializer
{
    private static readonly XmlWriterSettings XmlSettings = new()
    {
        Encoding = new UTF8Encoding(false),
        Indent = false,
        OmitXmlDeclaration = false,
        Async = true,
    };

    public static async Task WriteXmlAsync(Stream stream, PlexNode root)
    {
        await using var writer = XmlWriter.Create(stream, XmlSettings);
        await writer.WriteStartDocumentAsync().ConfigureAwait(false);
        await WriteXmlNodeAsync(writer, root).ConfigureAwait(false);
        await writer.WriteEndDocumentAsync().ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }

    public static async Task WriteJsonAsync(Stream stream, PlexNode root)
    {
        await using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WritePropertyName(root.Name);
        WriteJsonNode(writer, root);
        writer.WriteEndObject();
        await writer.FlushAsync().ConfigureAwait(false);
    }

    public static string ToXmlString(PlexNode root)
    {
        using var ms = new MemoryStream();
        WriteXmlAsync(ms, root).GetAwaiter().GetResult();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static string ToJsonString(PlexNode root)
    {
        using var ms = new MemoryStream();
        WriteJsonAsync(ms, root).GetAwaiter().GetResult();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static async Task WriteXmlNodeAsync(XmlWriter writer, PlexNode node)
    {
        await writer.WriteStartElementAsync(null, node.Name, null).ConfigureAwait(false);
        foreach (var (key, value) in node.Attributes)
        {
            await writer.WriteAttributeStringAsync(null, key, null, FormatXml(value)).ConfigureAwait(false);
        }

        foreach (var child in node.Children)
        {
            await WriteXmlNodeAsync(writer, child).ConfigureAwait(false);
        }

        await writer.WriteEndElementAsync().ConfigureAwait(false);
    }

    private static void WriteJsonNode(Utf8JsonWriter writer, PlexNode node)
    {
        writer.WriteStartObject();
        foreach (var (key, value) in node.Attributes)
        {
            writer.WritePropertyName(key);
            WriteJsonValue(writer, value);
        }

        // Group children by JSON key, preserving first-seen order.
        foreach (var group in node.Children.GroupBy(c => c.JsonKey))
        {
            writer.WritePropertyName(group.Key);
            if (node.JsonSingleChildren && group.Count() == 1)
            {
                // PMS writes the one item of a SearchResult as an object, not an array.
                WriteJsonNode(writer, group.First());
                continue;
            }

            writer.WriteStartArray();
            foreach (var child in group)
            {
                WriteJsonNode(writer, child);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void WriteJsonValue(Utf8JsonWriter writer, object value)
    {
        switch (value)
        {
            case bool b: writer.WriteBooleanValue(b); break;
            case int i: writer.WriteNumberValue(i); break;
            case long l: writer.WriteNumberValue(l); break;
            case float f: writer.WriteNumberValue(f); break;
            case double d: writer.WriteNumberValue(d); break;
            case decimal m: writer.WriteNumberValue(m); break;
            default: writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture)); break;
        }
    }

    private static string FormatXml(object value) => value switch
    {
        bool b => b ? "1" : "0",
        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
        float f => f.ToString("0.###", CultureInfo.InvariantCulture),
        IFormattable fmt => fmt.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
