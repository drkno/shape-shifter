using Jellyfin.Plugin.ShapeShifter.Api;
using Jellyfin.Plugin.ShapeShifter.Auth;
using Jellyfin.Plugin.ShapeShifter.Data;
using Jellyfin.Plugin.ShapeShifter.Discovery;
using Jellyfin.Plugin.ShapeShifter.Playback;
using Jellyfin.Plugin.ShapeShifter.PlexTv;
using Jellyfin.Plugin.ShapeShifter.Server;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Jellyfin.Plugin.ShapeShifter.Library;

namespace Jellyfin.Plugin.ShapeShifter;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IdMap>();
        serviceCollection.AddSingleton<PlexShares>();
        serviceCollection.AddHostedService<PlexShareSync>();
        serviceCollection.AddSingleton<PlexAuthService>();
        serviceCollection.AddSingleton<JellyfinLoopback>();
        serviceCollection.AddSingleton<Audiobooks>();
        serviceCollection.AddSingleton<MetadataMapper>();
        serviceCollection.AddSingleton<PlayQueueStore>();
        serviceCollection.AddSingleton<TranscodeSessions>();
        serviceCollection.AddSingleton<PlaybackReporter>();

        serviceCollection.AddSingleton<IPlexEndpoints, ServerEndpoints>();
        serviceCollection.AddSingleton<IPlexEndpoints, LibraryEndpoints>();
        serviceCollection.AddSingleton<IPlexEndpoints, PlaylistEndpoints>();
        serviceCollection.AddSingleton<IPlexEndpoints, CollectionEndpoints>();
        serviceCollection.AddSingleton<IPlexEndpoints, CompatEndpoints>();
        // Before ImageEndpoints: its /library/metadata/{id}/{kind} route would swallow /posters, /arts, /matches.
        serviceCollection.AddSingleton<IPlexEndpoints, MetadataEditEndpoints>();
        serviceCollection.AddSingleton<IPlexEndpoints, ImageEndpoints>();
        serviceCollection.AddSingleton<IPlexEndpoints, PlaybackEndpoints>();
        serviceCollection.AddSingleton<IPlexEndpoints, TimelineEndpoints>();
        serviceCollection.AddSingleton<IPlexEndpoints, PlayQueueEndpoints>();

        serviceCollection.AddSingleton<PlexTvClient>();
        serviceCollection.AddSingleton<PlexListenerService>();
        serviceCollection.AddSingleton<ConnectionPublisher>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<PlexListenerService>());
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<ConnectionPublisher>());
        serviceCollection.AddHostedService<GdmResponder>();
    }
}
