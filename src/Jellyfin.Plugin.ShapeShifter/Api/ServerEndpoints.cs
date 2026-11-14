using System.Net.WebSockets;
using System.Text;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ShapeShifter.Configuration;
using Jellyfin.Plugin.ShapeShifter.Plex;
using Jellyfin.Plugin.ShapeShifter.PlexTv;
using Jellyfin.Plugin.ShapeShifter.Server;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ShapeShifter.Api;

/// <summary>Server identity, capabilities and housekeeping endpoints.</summary>
public sealed class ServerEndpoints : IPlexEndpoints
{
    private readonly ILibraryManager _libraryManager;
    private readonly ISessionManager _sessionManager;
    private readonly MetadataMapper _mapper;
    private readonly PlexTvClient _plexTv;
    private readonly IServiceProvider _services;

    public ServerEndpoints(ILibraryManager libraryManager, ISessionManager sessionManager, MetadataMapper mapper, PlexTvClient plexTv, IServiceProvider services)
    {
        _libraryManager = libraryManager;
        _sessionManager = sessionManager;
        _mapper = mapper;
        _plexTv = plexTv;
        _services = services;
    }

    // Resolved on use: the publisher depends on the listener, which depends on these endpoints.
    private ConnectionPublisher Publisher => _services.GetRequiredService<ConnectionPublisher>();

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    public void Map(PlexRouter.IRouteBuilder routes)
    {
        routes.Get("/", Root)
            .Get("/identity", Identity, anonymous: true)
            .Get("/servers", Servers)
            .Get("/media/providers", MediaProviders)
            .Get("/myplex/account", MyPlexAccount)
            .Post("/myplex/claim", Claim)
            .Put("/myplex/refreshReachability", RefreshReachability)
            .Put("/updater/check", ctx => ctx.WriteEmpty())
            .Get("/:/prefs", Prefs)
            .Get("/:/prefs/get", Prefs)
            .Get("/system/agents", Agents)
            .Get("/status/sessions/background", ctx => ctx.WriteEmpty())
            .Put("/:/prefs", ctx => ctx.WriteEmpty())
            .Get("/accounts", Accounts)
            .Get("/accounts/{id}", Accounts)
            .Get("/clients", ctx => ctx.WriteEmpty())
            .Get("/devices", ctx => ctx.WriteEmpty())
            .Get("/system", ctx => ctx.WriteEmpty())
            .Get("/activities", ctx => ctx.WriteEmpty())
            .Get("/updater/status", ctx => ctx.Write(PlexNode.Container().Set("size", 0).Set("canInstall", false)))
            .Get("/butler", ctx => ctx.WriteEmpty())
            .Get("/:/websockets/notifications", Notifications)
            .Get("/:/eventsource/notifications", EventSource);
    }

    private static PlexNode ServerAttributes(PlexNode node)
    {
        var c = Config;
        return node
            .Set("allowCameraUpload", false)
            .Set("allowChannelAccess", false)
            .Set("allowMediaDeletion", false)
            .Set("allowSharing", true)
            .Set("allowSync", false)
            .Set("allowTuners", false)
            .Set("backgroundProcessing", false)
            .Set("certificate", !string.IsNullOrEmpty(c.CertificatePath))
            .Set("companionProxy", true)
            .Set("eventStream", true)
            .Set("friendlyName", c.FriendlyName)
            .Set("hubSearch", true)
            .Set("itemClusters", true)
            .Set("livetv", 0)
            .Set("machineIdentifier", c.MachineIdentifier)
            .Set("mediaProviders", true)
            .Set("multiuser", true)
            .Set("myPlex", !string.IsNullOrEmpty(c.ServerToken))
            .Set("myPlexMappingState", "mapped")
            .Set("myPlexSigninState", "ok")
            .Set("myPlexSubscription", true)
            .Set("myPlexUsername", c.OwnerPlexUsername)
            .Set("offlineTranscode", 1)
            .Set("platform", OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "MacOSX" : "Linux")
            .Set("platformVersion", Environment.OSVersion.Version.ToString())
            .Set("pluginHost", false)
            .Set("pushNotifications", false)
            .Set("readOnlyLibraries", true)
            .Set("streamingBrainABRVersion", 3)
            .Set("streamingBrainVersion", 2)
            .Set("sync", false)
            .Set("transcoderActiveVideoSessions", 0)
            .Set("transcoderAudio", true)
            .Set("transcoderLyrics", false)
            .Set("transcoderPhoto", true)
            .Set("transcoderSubtitles", true)
            .Set("transcoderVideo", true)
            .Set("transcoderVideoBitrates", "64,96,208,320,720,1500,2000,3000,4000,8000,10000,12000,20000")
            .Set("transcoderVideoQualities", "0,1,2,3,4,5,6,7,8,9,10,11,12")
            .Set("transcoderVideoResolutions", "128,128,160,240,320,480,768,720,720,1080,1080,1080,1080")
            .Set("updatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            .Set("updater", false)
            .Set("version", c.AdvertisedVersion)
            .Set("voiceSearch", false);
    }

    private Task Root(PlexContext ctx)
    {
        var node = ServerAttributes(PlexNode.Container());
        foreach (var (key, title) in new[] { ("activities", "activities"), ("butler", "butler"), ("channels", "channels"), ("clients", "clients"), ("hubs", "hubs"), ("library", "library"), ("playlists", "playlists"), ("search", "search"), ("servers", "servers"), ("system", "system") })
        {
            node.Add(new PlexNode("Directory").Set("count", 1).Set("key", key).Set("title", title));
        }

        return ctx.Write(node.WithSize());
    }

    private static Task Identity(PlexContext ctx) =>
        ctx.Write(PlexNode.Container()
            .Set("size", 0)
            .Set("claimed", !string.IsNullOrEmpty(Config.ServerToken))
            .Set("machineIdentifier", Config.MachineIdentifier)
            .Set("version", Config.AdvertisedVersion));

    private static Task Servers(PlexContext ctx)
    {
        var c = Config;
        var host = ctx.Http.Request.Host;
        var server = new PlexNode("Server")
            .Set("name", c.FriendlyName)
            .Set("host", host.Host)
            .Set("address", host.Host)
            .Set("port", host.Port ?? c.HttpPort)
            .Set("machineIdentifier", c.MachineIdentifier)
            .Set("version", c.AdvertisedVersion);
        return ctx.Write(PlexNode.Container().Add(server).WithSize());
    }

    private Task MediaProviders(PlexContext ctx)
    {
        var c = Config;
        var provider = new PlexNode("MediaProvider")
            .Set("identifier", MetadataMapper.LibraryIdentifier)
            .Set("title", "Library")
            .Set("types", "video,audio,photo")
            .Set("protocols", "stream,download");

        // Shaped like PMS 1.43's: a Home entry, then each library with the Pivots (tabs) that
        // mobile and TV apps build library screens from; Plex for Android shows "Something's
        // not right" for a library without them.
        var content = new PlexNode("Feature").Set("key", "/library/sections").Set("type", "content");
        content.Add(new PlexNode("Directory").Set("hubKey", "/hubs").Set("title", "Home"));
        foreach (var section in LibraryEndpoints.VisibleSections(_libraryManager, ctx.User))
        {
            var id = _mapper.SectionKey(section);
            var type = MetadataMapper.SectionType(section.CollectionType);
            // PMS sends the id as a string; Plex for Android (JavaScript) breaks on a number here.
            var (agent, scanner) = MetadataMapper.AgentAndScanner(type);
            var directory = new PlexNode("Directory")
                .Set("agent", agent)
                .Set("language", "en-US")
                .Set("refreshing", false)
                .Set("scanner", scanner)
                .Set("uuid", section.Id.ToString("D"))
                .Set("id", id.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Set("key", $"/library/sections/{id}")
                .Set("hubKey", $"/hubs/sections/{id}")
                .Set("type", type)
                .Set("title", section.Name)
                .Set("updatedAt", MetadataMapper.Unix(section.DateModified))
                .Set("scannedAt", MetadataMapper.Unix(section.DateModified));
            foreach (var (pivotId, key, pivotType, title, context) in Pivots(id, type))
            {
                directory.Add(new PlexNode("Pivot").Set("id", pivotId).Set("key", key).Set("type", pivotType).Set("title", title).Set("context", context).Set("symbol", PivotSymbol(pivotId)));
            }

            content.Add(directory);
        }

        provider.Add(content);
        foreach (var (type, key, flavor) in new (string, string?, string?)[]
        {
            ("search", "/hubs/search", null),
            ("match", "/library/matches", null),
            ("metadata", "/library/metadata", null),
            ("rate", "/:/rate", null),
            ("imagetranscoder", "/photo/:/transcode", null),
            ("promoted", "/hubs/promoted", null),
            ("continuewatching", "/hubs/continueWatching", null),
        })
        {
            provider.Add(new PlexNode("Feature").Set("key", key).Set("type", type).Set("flavor", flavor));
        }

        provider.Add(new PlexNode("Feature").Set("key", "/actions").Set("type", "actions")
            .Add(new PlexNode("Action").Set("id", "removeFromContinueWatching").Set("key", "/actions/removeFromContinueWatching")));
        foreach (var (type, key, flavor) in new (string, string?, string?)[]
        {
            ("playlist", "/playlists", "universal"),
            ("playqueue", "/playQueues", "universal"),
            ("collection", "/library/collections", null),
            ("timeline", "/:/timeline", null),
        })
        {
            provider.Add(new PlexNode("Feature").Set("key", key).Set("type", type).Set("flavor", flavor));
        }

        provider.Add(new PlexNode("Feature").Set("type", "manage"));
        provider.Add(new PlexNode("Feature").Set("key", "/library/search").Set("type", "universalsearch"));
        // Plex Web tests connections with this endpoint and reads the server's attributes
        // (myPlexSigninState decides "claimed") from the container, as PMS sends them.
        return ctx.Write(ServerAttributes(PlexNode.Container()).Add(provider).WithSize());
    }

    /// <summary>The library tabs PMS offers per section type.</summary>
    private static IEnumerable<(string Id, string Key, string Type, string Title, string Context)> Pivots(long id, string sectionType)
    {
        yield return ("recommended", $"/hubs/sections/{id}", "hub", "Recommended", "content.discover");
        var listType = sectionType switch { "show" => 2, "artist" => 8, "photo" => 13, _ => 1 };
        yield return ("library", $"/library/sections/{id}/all?type={listType}", "list", "Library", "content.library");
        if (sectionType is "movie" or "show")
        {
            yield return ("collections", $"/library/sections/{id}/collections", "list", "Collections", "content.collections");
            yield return ("categories", $"/library/sections/{id}/categories", "list", "Categories", "content.categories");
        }
        else if (sectionType == "artist")
        {
            yield return ("playlists", $"/playlists?type=15&sectionID={id}&playlistType=audio", "list", "Playlists", "content.playlists");
        }
    }

    private static string PivotSymbol(string pivotId) => pivotId switch
    {
        "recommended" => "star",
        "library" => "library",
        "collections" => "stack",
        "categories" => "category",
        _ => "playlist",
    };

    private static Task MyPlexAccount(PlexContext ctx)
    {
        var c = Config;
        return ctx.Write(new PlexNode("MyPlex")
            .Set("authToken", string.Empty)
            .Set("username", c.OwnerPlexUsername)
            .Set("mappingState", "mapped")
            .Set("mappingError", string.Empty)
            .Set("signInState", "ok")
            .Set("publicAddress", string.Empty)
            .Set("publicPort", c.HttpPort)
            .Set("privateAddress", string.Empty)
            .Set("privatePort", c.HttpPort)
            .Set("subscriptionActive", true)
            .Set("subscriptionState", "Active"));
    }

    /// <summary>
    /// POST /myplex/claim?token=claim-…: what Plex apps call when they think the server is
    /// unclaimed. Only the owner may re-claim a claimed server; takeover uses the imported identity.
    /// </summary>
    private async Task Claim(PlexContext ctx)
    {
        var token = ctx.Query("token");
        if (string.IsNullOrEmpty(token))
        {
            await ctx.Status(400).ConfigureAwait(false);
            return;
        }

        if (Config.Mode == IdentityMode.Takeover || (!string.IsNullOrEmpty(Config.ServerToken) && !ctx.Principal!.IsOwner))
        {
            await ctx.Status(403).ConfigureAwait(false);
            return;
        }

        try
        {
            await _plexTv.ClaimAsync(token, ctx.Http.RequestAborted).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            await ctx.Status(400).ConfigureAwait(false);
            return;
        }

        Publisher.Trigger();
        await ctx.Write(ServerAttributes(PlexNode.Container()).Set("size", 0)).ConfigureAwait(false);
    }

    private Task RefreshReachability(PlexContext ctx)
    {
        Publisher.Trigger();
        return ctx.WriteEmpty();
    }

    private static Task Prefs(PlexContext ctx)
    {
        var c = Config;
        var node = PlexNode.Container();
        var only = ctx.Query("id");
        foreach (var (id, value) in new[] { ("FriendlyName", c.FriendlyName), ("MachineIdentifier", c.MachineIdentifier), ("ManualPortMappingMode", "0"), ("secureConnections", "1") }
            .Where(p => only is null || p.Item1 == only))
        {
            node.Add(new PlexNode("Setting", "Setting").Set("id", id).Set("label", id).Set("type", "text").Set("value", value).Set("default", string.Empty).Set("hidden", true).Set("advanced", false));
        }

        return ctx.Write(node.WithSize());
    }

    /// <summary>The metadata agents PMS would offer for a library type (shown in library settings).</summary>
    private static Task Agents(PlexContext ctx)
    {
        var node = PlexNode.Container();
        foreach (var (identifier, name, mediaType) in new[]
        {
            ("tv.plex.agents.movie", "Plex Movie", 1),
            ("tv.plex.agents.series", "Plex Series", 2),
            ("tv.plex.agents.music", "Plex Music", 8),
            ("com.plexapp.agents.none", "Personal Media", 13),
        })
        {
            if (ctx.QueryInt("mediaType") is { } wanted && wanted != mediaType && !(wanted == 4 && mediaType == 2))
            {
                continue;
            }

            node.Add(new PlexNode("Agent", "Agent").Set("hasAttribution", false).Set("hasPrefs", false).Set("identifier", identifier).Set("name", name)
                .Add(new PlexNode("MediaType", "MediaType").Set("mediaType", mediaType).Set("name", name)));
        }

        return ctx.Write(node.WithSize());
    }

    private static Task Accounts(PlexContext ctx)
    {
        var p = ctx.Principal!;
        var account = new PlexNode("Account")
            .Set("id", int.TryParse(p.PlexAccountId, out var id) ? id : 1)
            .Set("key", $"/accounts/{p.PlexAccountId}")
            .Set("name", p.PlexUsername)
            .Set("defaultAudioLanguage", "en")
            .Set("autoSelectAudio", true)
            .Set("defaultSubtitleLanguage", "en")
            .Set("subtitleMode", 0);
        return ctx.Write(PlexNode.Container().Add(account).WithSize());
    }

    // Clients hold these open for live updates (scan progress, timeline changes). Keeping them
    // open with pings is enough for clients to consider the server healthy.
    /// <summary>
    /// PMS pings notification streams every 10 seconds, and its own event-stream client (which
    /// other PMS instances on the account use to follow this server) gives up after about 20
    /// seconds without data, so a slower ping makes it reconnect forever.
    /// </summary>
    private static readonly TimeSpan NotificationPingInterval = TimeSpan.FromSeconds(10);

    private static async Task Notifications(PlexContext ctx)
    {
        if (!ctx.Http.WebSockets.IsWebSocketRequest)
        {
            await ctx.Status(400).ConfigureAwait(false);
            return;
        }

        using var socket = await ctx.Http.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var ct = ctx.Http.RequestAborted;
        var buffer = new byte[4096];
        var receive = Task.Run(
            async () =>
            {
                while (socket.State == WebSocketState.Open)
                {
                    var r = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }
                }
            },
            ct);

        while (socket.State == WebSocketState.Open && !receive.IsCompleted)
        {
            await Task.WhenAny(receive, Task.Delay(NotificationPingInterval, ct)).ConfigureAwait(false);
            if (socket.State == WebSocketState.Open && !receive.IsCompleted)
            {
                var ping = Encoding.UTF8.GetBytes("{\"NotificationContainer\":{\"type\":\"ping\",\"size\":0}}");
                await socket.SendAsync(ping, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task EventSource(PlexContext ctx)
    {
        var response = ctx.Http.Response;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        var ct = ctx.Http.RequestAborted;
        while (!ct.IsCancellationRequested)
        {
            await response.WriteAsync("event: ping\ndata: {}\n\n", ct).ConfigureAwait(false);
            await response.Body.FlushAsync(ct).ConfigureAwait(false);
            await Task.Delay(NotificationPingInterval, ct).ConfigureAwait(false);
        }
    }
}
