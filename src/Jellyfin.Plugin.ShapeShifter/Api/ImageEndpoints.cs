using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.ShapeShifter.Data;
using Jellyfin.Plugin.ShapeShifter.Playback;
using Jellyfin.Plugin.ShapeShifter.Server;

namespace Jellyfin.Plugin.ShapeShifter.Api;

/// <summary>
/// Artwork. Plex clients request either the raw image (/library/metadata/{rk}/thumb/{ts}) or a
/// resized one via /photo/:/transcode?url=...; both are served by Jellyfin's image endpoint,
/// which already handles resizing and caching.
/// </summary>
public sealed partial class ImageEndpoints : IPlexEndpoints
{
    private readonly IdMap _ids;
    private readonly JellyfinLoopback _loopback;

    public ImageEndpoints(IdMap ids, JellyfinLoopback loopback)
    {
        _ids = ids;
        _loopback = loopback;
    }

    public void Map(PlexRouter.IRouteBuilder routes)
    {
        routes.Get("/library/metadata/{id}/{kind}/{ts}", ctx => Serve(ctx, ctx.Route("id"), ctx.Route("kind"), null, null))
            .Get("/library/metadata/{id}/{kind}", ctx => Serve(ctx, ctx.Route("id"), ctx.Route("kind"), null, null))
            .Get("/photo/:/transcode", Transcode);
    }

    private Task Transcode(PlexContext ctx)
    {
        var url = ctx.Query("url");
        if (url is null)
        {
            return ctx.Status(400);
        }

        // Only our own artwork paths are accepted; never fetch arbitrary URLs. plex.tv images
        // (account avatars) are redirected to rather than fetched, so this can't be used to
        // make the server request other hosts.
        var m = ArtworkPath().Match(url.Split('?')[0]);
        if (!m.Success)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var external) && external.Scheme == Uri.UriSchemeHttps
                && (external.Host == "plex.tv" || external.Host.EndsWith(".plex.tv", StringComparison.OrdinalIgnoreCase)))
            {
                ctx.Http.Response.Redirect(external.AbsoluteUri);
                return Task.CompletedTask;
            }

            return ctx.Status(404);
        }

        return Serve(ctx, m.Groups["id"].Value, m.Groups["kind"].Value, ctx.QueryInt("width"), ctx.QueryInt("height"));
    }

    private Task Serve(PlexContext ctx, string ratingKey, string kind, int? width, int? height)
    {
        var id = _ids.ResolveGuid(IdMap.Item, ratingKey);
        var imageType = kind switch
        {
            "thumb" => "Primary",
            "art" => "Backdrop",
            "banner" => "Banner",
            "clearLogo" => "Logo",
            "theme" => null,
            _ => null,
        };
        if (id is null || imageType is null)
        {
            return ctx.Status(404);
        }

        var query = new List<string> { "quality=90" };
        if (width is > 0)
        {
            query.Add("maxWidth=" + width.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (height is > 0)
        {
            query.Add("maxHeight=" + height.Value.ToString(CultureInfo.InvariantCulture));
        }

        return _loopback.ProxyAsync(ctx, $"/Items/{id.Value:N}/Images/{imageType}?{string.Join('&', query)}");
    }

    [GeneratedRegex(@"^/library/metadata/(?<id>\d+)/(?<kind>thumb|art|banner|clearLogo)(/\d+)?$")]
    private static partial Regex ArtworkPath();
}
