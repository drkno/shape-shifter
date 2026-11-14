using Jellyfin.Plugin.ShapeShifter.Api;
using Xunit;

namespace Jellyfin.Plugin.ShapeShifter.Tests;

public class MetadataEditTests
{
    private static MetadataEditRequest Parse(params (string Key, string Value)[] query) =>
        MetadataEditRequest.Parse(query.Select(q => new KeyValuePair<string, string?>(q.Key, q.Value)));

    [Fact]
    public void ParsesValuesAndLocks()
    {
        var edit = Parse(("type", "1"), ("id", "5"), ("title.value", "Heat"), ("title.locked", "1"), ("summary.value", ""), ("summary.locked", "0"));
        Assert.Equal("Heat", edit.Values["title"]);
        Assert.Equal(string.Empty, edit.Values["summary"]);
        Assert.True(edit.Locks["title"]);
        Assert.False(edit.Locks["summary"]);
        Assert.False(edit.Values.ContainsKey("type"));
    }

    [Fact]
    public void ParsesTagAddsInIndexOrderAndRemovals()
    {
        var edit = Parse(("genre[1].tag.tag", "Crime"), ("genre[0].tag.tag", "Drama"), ("genre[].tag.tag-", "Comedy,Romance"), ("genre.locked", "1"), ("collection[0].tag.tag", "Mann"));
        Assert.Equal(["Drama", "Crime"], edit.TagAdds["genre"]);
        Assert.Equal(["Comedy", "Romance"], edit.TagRemoves["genre"]);
        Assert.Equal(["Mann"], edit.TagAdds["collection"]);
        Assert.True(edit.Locks["genre"]);
    }

    [Fact]
    public void MatchGuidRoundTrips()
    {
        var ids = new Dictionary<string, string> { ["Tmdb"] = "949", ["Imdb"] = "tt0113277" };
        var parsed = MetadataEditEndpoints.ParseMatchGuid(MetadataEditEndpoints.MatchGuid(ids));
        Assert.Equal("949", parsed["Tmdb"]);
        Assert.Equal("tt0113277", parsed["imdb"]);
    }

    [Fact]
    public void SniffsUploadedImages()
    {
        Assert.Equal("image/jpeg", MetadataEditEndpoints.SniffImageType([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal("image/png", MetadataEditEndpoints.SniffImageType([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D]));
        Assert.Null(MetadataEditEndpoints.SniffImageType("<html>"u8));
    }
}
