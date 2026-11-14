using System.Net.Mime;
using System.Security.Cryptography.X509Certificates;
using Jellyfin.Plugin.ShapeShifter.Configuration;
using Jellyfin.Plugin.ShapeShifter.PlexTv;
using Jellyfin.Plugin.ShapeShifter.Server;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ShapeShifter.Api;

/// <summary>
/// Jellyfin-side admin API backing the settings page: what the listener is doing, what we'd
/// publish and what plex.tv currently lists. This is how takeover is checked before it
/// changes anything for real clients.
/// </summary>
[ApiController]
[Route("ShapeShifter")]
[Authorize(Policy = "RequiresElevation")]
[Produces(MediaTypeNames.Application.Json)]
public class StatusController : ControllerBase
{
    private readonly PlexListenerService _listener;
    private readonly PlexTvClient _plexTv;
    private readonly ConnectionPublisher _publisher;

    public StatusController(PlexListenerService listener, PlexTvClient plexTv, ConnectionPublisher publisher)
    {
        _listener = listener;
        _plexTv = plexTv;
        _publisher = publisher;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    [HttpGet("Status")]
    public async Task<ActionResult<object>> GetStatus(CancellationToken ct)
    {
        var c = Config;
        var certs = _listener.Certificates;
        List<PlexConnection> preview;
        object? plexTv;
        string? plexTvError = null;
        try
        {
            preview = await _plexTv.BuildConnectionsAsync(certs, ct).ConfigureAwait(false);
            var resource = await _plexTv.GetOwnResourceAsync(ct).ConfigureAwait(false);
            plexTv = resource;
        }
        catch (HttpRequestException ex)
        {
            preview = [];
            plexTv = null;
            plexTvError = ex.Message;
        }

        return new
        {
            mode = c.Mode.ToString(),
            machineIdentifier = c.MachineIdentifier,
            friendlyName = c.FriendlyName,
            claimed = !string.IsNullOrEmpty(c.ServerToken),
            owner = new { id = c.OwnerPlexAccountId, username = c.OwnerPlexUsername },
            listener = new { running = _listener.IsRunning, port = c.HttpPort, extraTlsPort = c.HttpsPort },
            certificates = new[] { ("plex.direct", certs?.PlexDirect), ("custom", certs?.Custom) }
                .Where(x => x.Item2 is not null)
                .Select(x => Describe(x.Item1, x.Item2!)),
            plexDirectHash = certs?.PlexDirectHash,
            warnings = _plexTv.Diagnose(certs),
            publishing = new
            {
                enabled = c.PublishConnections,
                skipReason = _publisher.LastSkipReason,
                lastPublished = _publisher.LastPublished,
                lastError = _publisher.LastError,
                lastPublishedConnections = _publisher.LastConnections,
                wouldPublish = preview,
            },
            plexTvResource = plexTv,
            plexTvError,
        };
    }

    public sealed record ClaimRequest(string Token);

    [HttpPost("Claim")]
    public async Task<ActionResult> Claim([FromBody] ClaimRequest request, CancellationToken ct)
    {
        try
        {
            await _plexTv.ClaimAsync(request.Token, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        _publisher.Trigger();
        return NoContent();
    }

    [HttpPost("Publish")]
    public ActionResult Publish()
    {
        _publisher.Trigger();
        return Accepted();
    }

    /// <summary>Starts over with a fresh side-by-side identity (the old one stays on plex.tv until removed there).</summary>
    [HttpPost("NewIdentity")]
    public ActionResult NewIdentity()
    {
        var c = Config;
        if (c.Mode != IdentityMode.SideBySide)
        {
            return BadRequest(new { error = "Only available in side-by-side mode." });
        }

        c.MachineIdentifier = Plugin.NewMachineIdentifier();
        c.ServerToken = string.Empty;
        c.OwnerPlexAccountId = string.Empty;
        Plugin.Instance!.SaveConfiguration();
        return Ok(new { machineIdentifier = c.MachineIdentifier });
    }

    private static object Describe(string kind, X509Certificate2 cert) => new
    {
        kind,
        subject = cert.Subject,
        names = CertificateStore.DnsNames(cert).Distinct(),
        notAfter = cert.NotAfter,
        expiresInDays = (int)(cert.NotAfter - DateTime.Now).TotalDays,
    };
}
