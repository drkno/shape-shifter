using Jellyfin.Plugin.ShapeShifter.PlexTv;
using Jellyfin.Plugin.ShapeShifter.Server;
using Xunit;

namespace Jellyfin.Plugin.ShapeShifter.Tests;

public class PlexTvTests
{
    [Theory]
    [InlineData("<user id=\"1\" authenticationToken=\"abc123\"/>", "abc123")]
    [InlineData("<?xml version=\"1.0\"?><MediaContainer><user authToken=\"xyz\"/></MediaContainer>", "xyz")]
    [InlineData("{\"authToken\":\"json-token\"}", "json-token")]
    [InlineData("<user id=\"1\"/>", null)]
    public void ExtractsClaimToken(string body, string? expected) =>
        Assert.Equal(expected, PlexTvClient.ExtractToken(body));

    [Fact]
    public void BuildsServerRegistrationFromRemoteConnection()
    {
        var xml = System.Xml.Linq.XElement.Parse(PlexTvClient.BuildServerRegistration("MyCoolServer", "abc", "1.41.3", [
            new("http://192.168.1.2:32400", "local"),
            new("https://plex.mydomain.com:443", "custom"),
        ], [
            new(50000000, "movie", "Movies", "b05fd3f5-cea5-424f-ba58-66a4cd3f1222", 100, 200, ["/media/Movies"]),
            new(50000001, "show", "TV Shows", "6d6d172c-b513-4a45-b7ad-ceaa9174e3a2", 100, 200, ["/media/TV", "/media/TV2"]),
        ], DateTimeOffset.FromUnixTimeSeconds(1_794_631_114))!);
        var server = xml.Element("Server")!;
        Assert.Equal("3", (string?)xml.Attribute("size"));
        Assert.Equal("1794631114000000", (string?)server.Attribute("date"));

        // Libraries are Directory siblings of Server, as PMS sends them; plex.tv makes sections from these.
        var directories = xml.Elements("Directory").ToList();
        Assert.Equal(["50000000", "50000001"], directories.Select(d => (string)d.Attribute("key")!));
        Assert.Equal("tv.plex.agents.series", (string?)directories[1].Attribute("agent"));
        var locations = directories[1].Elements("Location").ToList();
        Assert.Equal(2, locations.Count);
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("/media/TV"u8.ToArray())).ToLowerInvariant(),
            (string?)locations[0].Attribute("pathHash"));
        Assert.Equal("plex.mydomain.com", (string?)server.Attribute("address"));
        Assert.Equal("443", (string?)server.Attribute("port"));
        Assert.Equal("https", (string?)server.Attribute("scheme"));
        Assert.Equal("192.168.1.2", (string?)server.Attribute("localAddresses"));
        Assert.Equal("abc", (string?)server.Attribute("machineIdentifier"));
        Assert.Null(PlexTvClient.BuildServerRegistration("x", "abc", "1", [], [], DateTimeOffset.UtcNow));
    }

    [Fact]
    public void BuildsPlexDirectUris() =>
        Assert.Equal("https://192-168-1-20.abcdef0123.plex.direct:32400", PlexTvClient.PlexDirect("192.168.1.20", "abcdef0123", 32400));

    [Theory]
    [InlineData("*.abc.plex.direct", "192-168-1-2.abc.plex.direct", true)]
    [InlineData("*.abc.plex.direct", "a.b.abc.plex.direct", false)]
    [InlineData("*.abc.plex.direct", "abc.plex.direct", false)]
    [InlineData("plex.example.com", "PLEX.example.com", true)]
    [InlineData("plex.example.com", "other.example.com", false)]
    public void MatchesCertificateNames(string pattern, string host, bool expected) =>
        Assert.Equal(expected, CertificateStore.Matches(pattern, host));
}
