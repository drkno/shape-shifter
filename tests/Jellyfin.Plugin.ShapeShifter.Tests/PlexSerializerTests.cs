using System.Text.Json;
using System.Xml.Linq;
using Jellyfin.Plugin.ShapeShifter.Plex;
using Xunit;

namespace Jellyfin.Plugin.ShapeShifter.Tests;

public class PlexSerializerTests
{
    private static PlexNode Sample()
    {
        var video = PlexNode.Metadata("Video")
            .Set("ratingKey", "42")
            .Set("title", "Heat & Dust")
            .Set("duration", 7_200_000L)
            .Set("audienceRating", 8.25)
            .Set("summary", null)
            .Set("summary", string.Empty)
            .AddTag("Genre", "Drama");
        video.Add(new PlexNode("Media").Set("id", 7).Add(new PlexNode("Part").Set("id", 7).Set("key", "/library/parts/7/1/file.mkv")));
        return PlexNode.Container().Set("allowSync", false).Add(video).Add(PlexNode.Metadata("Directory").Set("ratingKey", "43")).WithSize(totalSize: 100, offset: 0);
    }

    [Fact]
    public void XmlMatchesPmsShape()
    {
        var doc = XDocument.Parse(PlexSerializer.ToXmlString(Sample()));
        var root = doc.Root!;
        Assert.Equal("MediaContainer", root.Name.LocalName);
        Assert.Equal("0", (string?)root.Attribute("allowSync"));
        Assert.Equal("2", (string?)root.Attribute("size"));
        Assert.Equal("100", (string?)root.Attribute("totalSize"));

        var video = root.Element("Video")!;
        Assert.Equal("Heat & Dust", (string?)video.Attribute("title"));
        Assert.Equal("8.25", (string?)video.Attribute("audienceRating"));
        Assert.Null(video.Attribute("summary"));
        Assert.Equal("Drama", (string?)video.Element("Genre")!.Attribute("tag"));
        Assert.Equal("/library/parts/7/1/file.mkv", (string?)video.Element("Media")!.Element("Part")!.Attribute("key"));
        Assert.NotNull(root.Element("Directory"));
    }

    [Fact]
    public void JsonGroupsMetadataAndKeepsTypes()
    {
        using var doc = JsonDocument.Parse(PlexSerializer.ToJsonString(Sample()));
        var mc = doc.RootElement.GetProperty("MediaContainer");
        Assert.False(mc.GetProperty("allowSync").GetBoolean());
        Assert.Equal(2, mc.GetProperty("size").GetInt32());

        var metadata = mc.GetProperty("Metadata");
        Assert.Equal(2, metadata.GetArrayLength());
        Assert.Equal("42", metadata[0].GetProperty("ratingKey").GetString());
        Assert.Equal(7_200_000L, metadata[0].GetProperty("duration").GetInt64());
        Assert.Equal("Drama", metadata[0].GetProperty("Genre")[0].GetProperty("tag").GetString());
        Assert.Equal(7, metadata[0].GetProperty("Media")[0].GetProperty("Part")[0].GetProperty("id").GetInt32());
        Assert.False(metadata[0].TryGetProperty("summary", out _));
    }

    [Fact]
    public void SearchResultWritesItsItemAsAnObject()
    {
        var root = PlexNode.Container()
            .Add(new PlexNode("SearchResult") { JsonSingleChildren = true }.Set("score", 0.9).Add(PlexNode.Metadata("Video").Set("ratingKey", "1")))
            .Add(new PlexNode("SearchResult") { JsonSingleChildren = true }.Set("score", 0.8).Add(new PlexNode("Directory").Set("tag", "Someone")));

        using var doc = JsonDocument.Parse(PlexSerializer.ToJsonString(root));
        var results = doc.RootElement.GetProperty("MediaContainer").GetProperty("SearchResult");
        Assert.Equal(JsonValueKind.Object, results[0].GetProperty("Metadata").ValueKind);
        Assert.Equal("1", results[0].GetProperty("Metadata").GetProperty("ratingKey").GetString());
        Assert.Equal("Someone", results[1].GetProperty("Directory").GetProperty("tag").GetString());
    }
}
