using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShapeShifter.Discovery;

/// <summary>
/// "G'Day Mate" LAN discovery. Clients broadcast/multicast "M-SEARCH * HTTP/1.1" to udp/32414
/// (239.0.0.250) and servers reply with a small HTTP-like header block.
/// </summary>
public sealed class GdmResponder : BackgroundService
{
    private const int GdmPort = 32414;
    private static readonly IPAddress GdmMulticast = IPAddress.Parse("239.0.0.250");

    private readonly ILogger<GdmResponder> _logger;

    public GdmResponder(ILogger<GdmResponder> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.EnableGdm)
        {
            return;
        }

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, GdmPort));
            udp.JoinMulticastGroup(GdmMulticast);
        }
        catch (SocketException ex)
        {
            _logger.LogWarning(ex, "GDM discovery disabled: could not bind udp/{Port} (is Plex Media Server still running?)", GdmPort);
            return;
        }

        _logger.LogInformation("GDM discovery listening on udp/{Port}", GdmPort);
        while (!stoppingToken.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await udp.ReceiveAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "GDM receive failed");
                continue;
            }

            var message = Encoding.ASCII.GetString(received.Buffer);
            if (!message.StartsWith("M-SEARCH", StringComparison.Ordinal))
            {
                continue;
            }

            var reply = Encoding.ASCII.GetBytes(BuildReply());
            try
            {
                await udp.SendAsync(reply, received.RemoteEndPoint, stoppingToken).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "GDM reply to {Remote} failed", received.RemoteEndPoint);
            }
        }
    }

    private static string BuildReply()
    {
        var c = Plugin.Instance!.Configuration;
        var port = c.HttpPort > 0 ? c.HttpPort : c.HttpsPort;
        return "HTTP/1.0 200 OK\r\n"
            + "Content-Type: plex/media-server\r\n"
            + $"Resource-Identifier: {c.MachineIdentifier}\r\n"
            + $"Name: {c.FriendlyName}\r\n"
            + $"Port: {port}\r\n"
            + $"Updated-At: {DateTimeOffset.UtcNow.ToUnixTimeSeconds()}\r\n"
            + $"Version: {c.AdvertisedVersion}\r\n"
            + "\r\n";
    }
}
