using Jellyfin.Plugin.ShapeShifter.Server;
using Xunit;

namespace Jellyfin.Plugin.ShapeShifter.Tests;

public class PlexRouterTests
{
    private static readonly Func<PlexContext, Task> Noop = _ => Task.CompletedTask;

    private static PlexRouter Router()
    {
        var router = new PlexRouter();
        router.Builder
            .Get("/library/metadata/{ids}", Noop)
            .Get("/library/metadata/{id}/children", Noop)
            .Any("/:/timeline", Noop)
            .Get("/video/:/transcode/universal/start.m3u8", Noop)
            .Get("/video/:/transcode/universal/session/{session}/{secret}/{*rest}", Noop, anonymous: true)
            .Get("/library/parts/{part}/{*rest}", Noop);
        return router;
    }

    [Theory]
    [InlineData("GET", "/library/metadata/123", "ids", "123")]
    [InlineData("GET", "/library/metadata/1,2,3", "ids", "1,2,3")]
    [InlineData("HEAD", "/library/metadata/5/", "ids", "5")]
    [InlineData("GET", "/library/metadata/77/children", "id", "77")]
    [InlineData("GET", "/library/parts/9/1700000000/file.mkv", "rest", "1700000000/file.mkv")]
    [InlineData("GET", "/video/:/transcode/universal/session/abc/s3cr3t/hls1/main/0.ts", "rest", "hls1/main/0.ts")]
    public void MatchesRouteValues(string method, string path, string name, string expected)
    {
        var match = Router().Match(method, path);
        Assert.NotNull(match);
        Assert.Equal(expected, match.Values[name]);
    }

    [Fact]
    public void ColonPathsMatchLiterally()
    {
        var router = Router();
        Assert.NotNull(router.Match("POST", "/:/timeline"));
        Assert.NotNull(router.Match("GET", "/video/:/transcode/universal/start.m3u8"));
        Assert.Null(router.Match("GET", "/video/:/transcode/universal/startXm3u8"));
    }

    [Fact]
    public void MethodMustMatch()
    {
        Assert.Null(Router().Match("POST", "/library/metadata/1"));
    }

    [Fact]
    public void AnonymousFlagIsKept()
    {
        var match = Router().Match("GET", "/video/:/transcode/universal/session/a/b/c.ts");
        Assert.True(match!.Route.Anonymous);
        Assert.False(Router().Match("GET", "/library/metadata/1")!.Route.Anonymous);
    }
}
