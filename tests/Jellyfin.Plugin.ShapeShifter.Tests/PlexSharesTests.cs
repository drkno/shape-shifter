using Jellyfin.Plugin.ShapeShifter.Auth;
using Xunit;

namespace Jellyfin.Plugin.ShapeShifter.Tests;

public class PlexSharesTests
{
    private const string SharedServers = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MediaContainer friendlyName="myPlex" identifier="com.plexapp.plugins.myplex" machineIdentifier="x" size="3">
          <SharedServer id="1" username="alice" email="a@example.com" userID="111" accessToken="tok-alice" name="MyCoolServer" allLibraries="0" owned="0">
            <Section id="123456788" key="50000000" title="Movies" type="movie" shared="1"/>
            <Section id="123456789" key="50000001" title="TV Shows" type="show" shared="0"/>
          </SharedServer>
          <SharedServer id="2" username="bob smith" userID="222" accessToken="tok-bob" allLibraries="1"/>
          <SharedServer id="3" username="pending" userID="333" accessToken="" allLibraries="1"/>
        </MediaContainer>
        """;

    [Fact]
    public void ParsesSharesWithTheirSharedLibraries()
    {
        var shares = PlexShares.Parse(SharedServers);

        // An invite not yet accepted has no token and isn't a usable share.
        Assert.Equal(2, shares.Count);
        Assert.Equal(("111", "alice", "tok-alice", false), (shares[0].AccountId, shares[0].Username, shares[0].AccessToken, shares[0].AllLibraries));
        Assert.Equal([50000000L], shares[0].SectionKeys);
        Assert.True(shares[1].AllLibraries);
        Assert.Empty(shares[1].SectionKeys);
    }

    [Theory]
    [InlineData("alice", "111", "alice")]
    [InlineData("bob smith", "222", "bob smith")]
    [InlineData("", "333", "plex-333")]
    [InlineData(" trailing ", "444", "plex-444")]
    [InlineData("emoji\u2728", "555", "plex-555")]
    public void NamesUsersAfterTheirPlexUsername(string username, string id, string expected) =>
        Assert.Equal(expected, PlexShares.JellyfinName(new PlexShare(id, System.Text.RegularExpressions.Regex.Unescape(username), "t", true, [])));
}
