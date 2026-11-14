using Jellyfin.Plugin.ShapeShifter.Plex;
using Jellyfin.Plugin.ShapeShifter.Server;

namespace Jellyfin.Plugin.ShapeShifter.Api;

/// <summary>
/// PMS endpoints for features Jellyfin doesn't provide here (DVR, subtitle downloads, butler
/// jobs), answered the way a PMS without them answers, so clients that probe them (Plezy
/// probes all of these) degrade quietly instead of logging errors or treating the server as broken.
/// </summary>
public sealed class CompatEndpoints : IPlexEndpoints
{
    public void Map(PlexRouter.IRouteBuilder routes)
    {
        routes.Get("/livetv/dvrs", ctx => ctx.WriteEmpty())
            .Get("/livetv/sessions", ctx => ctx.WriteEmpty())
            .Get("/media/subscriptions", ctx => ctx.WriteEmpty())
            .Get("/media/subscriptions/scheduled", ctx => ctx.WriteEmpty())
            .Get("/media/subscriptions/template", ctx => ctx.WriteEmpty())
            .Get("/media/grabbers/operations", ctx => ctx.WriteEmpty())
            .Delete("/activities/{id}", ctx => ctx.WriteEmpty())

            // Online subtitle search (OpenSubtitles via PMS): nothing to offer.
            .Get("/library/metadata/{id}/subtitles", ctx => ctx.Write(PlexNode.Container().Set("size", 0)))
            .Put("/library/metadata/{id}/subtitles", ctx => ctx.Status(404))

            // Library maintenance Jellyfin does on its own schedule.
            .Put("/library/sections/{section}/analyze", ctx => ctx.WriteEmpty())
            .Put("/library/sections/{section}/emptyTrash", ctx => ctx.WriteEmpty())
            .Put("/library/optimize", ctx => ctx.WriteEmpty())
            .Put("/library/clean/bundles", ctx => ctx.WriteEmpty());
    }
}
