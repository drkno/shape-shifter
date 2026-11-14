using Jellyfin.Plugin.ShapeShifter.Api;
using Jellyfin.Plugin.ShapeShifter.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Jellyfin.Plugin.ShapeShifter.Server;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShapeShifter.PlexTv;

/// <summary>Keeps plex.tv's record of our connections current, like PMS does.</summary>
public sealed class ConnectionPublisher : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly PlexTvClient _plexTv;
    private readonly PlexListenerService _listener;
    private readonly ILibraryManager _libraryManager;
    private readonly MetadataMapper _mapper;
    private readonly ILogger<ConnectionPublisher> _logger;
    private readonly SemaphoreSlim _wake = new(0, 1);

    public ConnectionPublisher(PlexTvClient plexTv, PlexListenerService listener, ILibraryManager libraryManager, MetadataMapper mapper, ILogger<ConnectionPublisher> logger)
    {
        _plexTv = plexTv;
        _listener = listener;
        _libraryManager = libraryManager;
        _mapper = mapper;
        _logger = logger;
    }

    public DateTime? LastPublished { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Gets why the last publish didn't happen, or null if it ran.</summary>
    public string? LastSkipReason { get; private set; }

    public IReadOnlyList<PlexConnection> LastConnections { get; private set; } = [];

    /// <summary>Publishes now instead of waiting for the next interval.</summary>
    public void Trigger()
    {
        if (_wake.CurrentCount == 0)
        {
            _wake.Release();
        }
    }

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged += (_, _) => Trigger();
        }

        // PMS re-registers when a library is added or removed, so plex.tv's sharing page follows.
        _libraryManager.ItemAdded += OnLibraryChanged;
        _libraryManager.ItemRemoved += OnLibraryChanged;
        stoppingToken.Register(() =>
        {
            _libraryManager.ItemAdded -= OnLibraryChanged;
            _libraryManager.ItemRemoved -= OnLibraryChanged;
        });

        // Give the listener a moment to load certificates.
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            await PublishOnceAsync(stoppingToken).ConfigureAwait(false);
            await _wake.WaitAsync(Interval, stoppingToken).ConfigureAwait(false);
        }
    }

    private void OnLibraryChanged(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is CollectionFolder)
        {
            Trigger();
        }
    }

    private List<RegisteredLibrary> Libraries() =>
        LibraryEndpoints.AllSections(_libraryManager)
            .Select(s => new RegisteredLibrary(
                _mapper.SectionKey(s),
                MetadataMapper.SectionType(s.CollectionType),
                s.Name,
                s.Id.ToString("D"),
                MetadataMapper.Unix(s.DateCreated),
                MetadataMapper.Unix(s.DateModified),
                s.PhysicalLocations.ToList()))
            .ToList();

    private async Task PublishOnceAsync(CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        var skip = config switch
        {
            null => "plugin not loaded",
            { PublishConnections: false } => "publishing is turned off",
            { ServerToken.Length: 0 } => "no server token: claim the server (side-by-side) or import PMS (takeover)",
            _ when !_listener.IsRunning => "the Plex listener isn't running",
            _ => null,
        };
        if (skip != LastSkipReason && skip is not null)
        {
            // Logged once per change, so a server that never shows up on plex.tv says why.
            _logger.LogInformation("Not publishing connections to plex.tv: {Reason}", skip);
        }

        LastSkipReason = skip;
        if (skip is not null)
        {
            return;
        }

        foreach (var warning in _plexTv.Diagnose(_listener.Certificates))
        {
            _logger.LogWarning("Connection settings: {Warning}", warning);
        }

        try
        {
            var connections = await _plexTv.BuildConnectionsAsync(_listener.Certificates, ct).ConfigureAwait(false);
            await _plexTv.PublishAsync(connections, ct).ConfigureAwait(false);
            await _plexTv.RegisterServerAsync(connections, Libraries(), ct).ConfigureAwait(false);
            LastConnections = connections;
            LastPublished = DateTime.UtcNow;
            LastError = null;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            LastError = ex.Message;
            _logger.LogWarning(ex, "Publishing connections to plex.tv failed");
        }
    }
}
