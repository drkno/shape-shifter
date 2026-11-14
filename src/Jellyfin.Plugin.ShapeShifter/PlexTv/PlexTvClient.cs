using System.Security.Cryptography;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Jellyfin.Plugin.ShapeShifter.Configuration;
using Jellyfin.Plugin.ShapeShifter.Server;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShapeShifter.PlexTv;

/// <summary>A connection URI as published to plex.tv.</summary>
public sealed record PlexConnection(string Uri, string Kind);

/// <summary>
/// The parts of plex.tv that a PMS itself talks to: claiming a server with a claim token and
/// publishing the connection URIs clients are told to use. Reverse-engineered; the status page
/// shows what plex.tv ends up listing so it can be checked against a live account.
/// </summary>
public sealed class PlexTvClient
{
    private const string PlexTv = "https://plex.tv";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PlexTvClient> _logger;

    public PlexTvClient(IHttpClientFactory httpClientFactory, ILogger<PlexTvClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    private HttpRequestMessage Request(HttpMethod method, string pathAndQuery, string? token)
    {
        var c = Config;
        var request = new HttpRequestMessage(method, PlexTv + pathAndQuery);
        request.Headers.Add("X-Plex-Client-Identifier", c.MachineIdentifier);
        request.Headers.Add("X-Plex-Product", "Plex Media Server");
        request.Headers.Add("X-Plex-Version", c.AdvertisedVersion);
        request.Headers.Add("X-Plex-Provides", "server");
        request.Headers.Add("X-Plex-Platform", OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "MacOSX" : "Linux");
        request.Headers.Add("X-Plex-Device-Name", c.FriendlyName);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Add("X-Plex-Token", token);
        }

        return request;
    }

    /// <summary>
    /// Exchanges a claim token (from https://plex.tv/claim, valid ~4 minutes) for a server token,
    /// attaching this server identity to the signed-in account.
    /// </summary>
    public async Task<string> ClaimAsync(string claimToken, CancellationToken ct)
    {
        if (Config.Mode == IdentityMode.Takeover)
        {
            throw new InvalidOperationException("Claiming is for side-by-side mode; takeover uses the imported PMS token.");
        }

        using var request = Request(HttpMethod.Post, "/api/claim/exchange?token=" + Uri.EscapeDataString(claimToken.Trim()), null);
        using var response = await _httpClientFactory.CreateClient().SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"plex.tv rejected the claim token ({(int)response.StatusCode}). Claim tokens expire after a few minutes; get a fresh one.");
        }

        var token = ExtractToken(body) ?? throw new InvalidOperationException("plex.tv accepted the claim but returned no token");
        var c = Config;
        c.ServerToken = token;
        c.OwnerPlexAccountId = string.Empty;
        Plugin.Instance!.SaveConfiguration();
        _logger.LogInformation("Claimed server {Name} ({MachineId}) on plex.tv", c.FriendlyName, c.MachineIdentifier);
        return token;
    }

    internal static string? ExtractToken(string body)
    {
        body = body.TrimStart();
        if (body.StartsWith('{'))
        {
            using var json = JsonDocument.Parse(body);
            foreach (var name in new[] { "authToken", "authenticationToken", "token" })
            {
                if (json.RootElement.TryGetProperty(name, out var v) && v.GetString() is { Length: > 0 } s)
                {
                    return s;
                }
            }

            return null;
        }

        var root = XDocument.Parse(body).Root;
        return root?.DescendantsAndSelf()
            .SelectMany(e => e.Attributes())
            .FirstOrDefault(a => a.Name.LocalName is "authenticationToken" or "authToken")?.Value;
    }

    /// <summary>
    /// Settings that are accepted but won't do what was probably intended. Shown on the status
    /// page and logged when publishing, because plex.tv gives no feedback about them.
    /// </summary>
    public List<string> Diagnose(CertificateStore? certs)
    {
        var c = Config;
        var warnings = new List<string>();
        if (string.IsNullOrEmpty(c.ServerToken))
        {
            warnings.Add(c.Mode == IdentityMode.SideBySide
                ? "Not claimed: get a claim token from https://plex.tv/claim and press Claim."
                : "No server token: run the PMS import with 'take over identity'.");
        }

        if (!string.IsNullOrEmpty(c.PublicAddress) && !IPAddress.TryParse(c.PublicAddress, out _))
        {
            warnings.Add($"Public address '{c.PublicAddress}' isn't an IP address, so it's ignored. URLs such as a reverse proxy address belong in Custom server access URLs.");
        }

        if (c.PublicPort > 0 && certs?.PlexDirectHash is null)
        {
            warnings.Add("Public port is set, but public connections are plex.direct URLs, which need the plex.direct certificate (takeover mode). Use a custom server access URL instead.");
        }

        foreach (var custom in c.CustomConnections.Where(u => !string.IsNullOrWhiteSpace(u)))
        {
            if (!Uri.TryCreate(custom.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                warnings.Add($"Custom server access URL '{custom}' must be an absolute http(s) URL.");
            }
        }

        if (c.LanAddresses.All(string.IsNullOrWhiteSpace) && InContainer())
        {
            warnings.Add($"Running in a container: detected LAN addresses ({string.Join(", ", LanAddresses())}) are the container's own. Set LAN addresses to the host's address, with the port the container publishes.");
        }

        if (c.CustomConnections.All(string.IsNullOrWhiteSpace) && (c.PublicPort == 0 || certs?.PlexDirectHash is null))
        {
            warnings.Add("No remote connection: only LAN addresses will be published, so the server is reachable only on the local network.");
        }

        return warnings;
    }

    private static bool InContainer() =>
        File.Exists("/.dockerenv") || File.Exists("/run/.containerenv") || Environment.GetEnvironmentVariable("container") is not null;

    /// <summary>The connection set to publish, in the order PMS lists them (local first).</summary>
    public async Task<List<PlexConnection>> BuildConnectionsAsync(CertificateStore? certs, CancellationToken ct)
    {
        var c = Config;
        var result = new List<PlexConnection>();
        var hash = certs?.PlexDirectHash;
        var plainAllowed = c.SecureConnections != SecureConnections.Required;
        var tlsAvailable = certs?.Any == true && c.SecureConnections != SecureConnections.Disabled;

        foreach (var ip in LanAddresses())
        {
            if (tlsAvailable && hash is not null)
            {
                result.Add(new(PlexDirect(ip, hash, c.HttpPort), "local"));
            }

            if (plainAllowed)
            {
                result.Add(new($"http://{ip}:{c.HttpPort}", "local"));
            }
        }

        if (c.PublicPort > 0 && tlsAvailable && hash is not null)
        {
            var publicIp = string.IsNullOrEmpty(c.PublicAddress) ? await GetPublicAddressAsync(ct).ConfigureAwait(false) : c.PublicAddress;
            if (publicIp is not null)
            {
                result.Add(new(PlexDirect(publicIp, hash, c.PublicPort), "public"));
            }
        }

        foreach (var custom in c.CustomConnections.Where(u => !string.IsNullOrWhiteSpace(u)))
        {
            result.Add(new(custom.Trim().TrimEnd('/'), "custom"));
        }

        return result;
    }

    public async Task PublishAsync(IReadOnlyList<PlexConnection> connections, CancellationToken ct)
    {
        var c = Config;
        if (string.IsNullOrEmpty(c.ServerToken))
        {
            throw new InvalidOperationException("No server token: claim the server (side-by-side) or import PMS (takeover) first.");
        }

        if (connections.Count == 0)
        {
            // plex.tv answers 422 to an empty list, and it would hide the server anyway.
            _logger.LogWarning("No connections to publish to plex.tv");
            return;
        }

        var query = new StringBuilder();
        foreach (var conn in connections)
        {
            query.Append(query.Length == 0 ? '?' : '&').Append("Connection[][uri]=").Append(Uri.EscapeDataString(conn.Uri));
        }

        var tls = connections.Any(x => x.Uri.StartsWith("https:", StringComparison.Ordinal));
        query.Append("&httpsEnabled=").Append(tls ? '1' : '0');
        query.Append("&httpsRequired=").Append(c.SecureConnections == SecureConnections.Required ? '1' : '0');
        query.Append("&name=").Append(Uri.EscapeDataString(c.FriendlyName));
        query.Append("&productVersion=").Append(Uri.EscapeDataString(c.AdvertisedVersion));

        using var request = Request(HttpMethod.Put, $"/devices/{c.MachineIdentifier}{query}", c.ServerToken);
        using var response = await _httpClientFactory.CreateClient().SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"plex.tv refused the connection update ({(int)response.StatusCode}): {body}");
        }

        _logger.LogInformation("Published {Count} connections to plex.tv: {Uris}", connections.Count, string.Join(", ", connections.Select(x => x.Uri)));
    }

    /// <summary>
    /// Registers the server in plex.tv's server registry (/api/servers). Clients only get an
    /// accessToken for an owned server from /api/v2/resources once it's listed there; without
    /// one, Plex Web shows "Not authorised" without ever contacting the server. Found by testing
    /// against plex.tv: an XML MediaContainer body is accepted (201), form or query parameters
    /// get 422. Posting again updates the entry.
    /// </summary>
    public async Task RegisterServerAsync(IReadOnlyList<PlexConnection> connections, IReadOnlyList<RegisteredLibrary> libraries, CancellationToken ct)
    {
        var c = Config;
        var body = BuildServerRegistration(c.FriendlyName, c.MachineIdentifier, c.AdvertisedVersion, connections, libraries, DateTimeOffset.UtcNow);
        if (body is null)
        {
            _logger.LogWarning("Not registering with plex.tv's server registry: no connection to register");
            return;
        }

        using var request = Request(HttpMethod.Post, "/servers.xml?auth_token=" + Uri.EscapeDataString(c.ServerToken), c.ServerToken);
        request.Content = new StringContent(body, Encoding.UTF8, "application/xml");
        using var response = await _httpClientFactory.CreateClient().SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"plex.tv refused the server registration ({(int)response.StatusCode})");
        }

        _logger.LogInformation("Registered {Name} with {Count} libraries in plex.tv's server registry (HTTP {Status})", c.FriendlyName, libraries.Count, (int)response.StatusCode);
    }

    /// <summary>
    /// The registry entry, in the shape PMS sends (captured from PMS 1.43): a Server element with
    /// the preferred remote connection as address/port/scheme (falling back to the first
    /// connection) and the LAN addresses, followed by one Directory per library as a sibling.
    /// plex.tv creates its section records, which sharing (Manage Library Access) is built on,
    /// from those Directory elements. Paths are only sent as SHA-256 hashes, as PMS does.
    /// </summary>
    internal static string? BuildServerRegistration(string name, string machineIdentifier, string version, IReadOnlyList<PlexConnection> connections, IReadOnlyList<RegisteredLibrary> libraries, DateTimeOffset now)
    {
        var main = connections.FirstOrDefault(x => x.Kind != "local") ?? connections.FirstOrDefault();
        if (main is null || !Uri.TryCreate(main.Uri, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var local = connections.Where(x => x.Kind == "local")
            .Select(x => Uri.TryCreate(x.Uri, UriKind.Absolute, out var u) ? u.Host : null)
            .OfType<string>()
            .Where(h => IPAddress.TryParse(h, out _))
            .Distinct();

        var server = new XElement(
            "Server",
            new XAttribute("name", name),
            new XAttribute("address", uri.Host),
            new XAttribute("port", uri.Port),
            new XAttribute("scheme", uri.Scheme),
            new XAttribute("host", uri.Host),
            new XAttribute("localAddresses", string.Join(',', local)),
            new XAttribute("machineIdentifier", machineIdentifier),
            new XAttribute("version", version),
            new XAttribute("relay", 0),
            new XAttribute("date", (now.ToUnixTimeMilliseconds() * 1000).ToString(CultureInfo.InvariantCulture)));

        var container = new XElement("MediaContainer", new XAttribute("size", 1 + libraries.Count), server);
        var locationId = 1;
        foreach (var library in libraries)
        {
            var (agent, scanner) = Api.MetadataMapper.AgentAndScanner(library.Type);
            var directory = new XElement(
                "Directory",
                new XAttribute("key", library.Key),
                new XAttribute("type", library.Type),
                new XAttribute("title", library.Title),
                new XAttribute("agent", agent),
                new XAttribute("scanner", scanner),
                new XAttribute("language", "en-US"),
                new XAttribute("uuid", library.Uuid),
                new XAttribute("updatedAt", library.UpdatedAt),
                new XAttribute("createdAt", library.CreatedAt),
                new XAttribute("scannedAt", library.UpdatedAt),
                new XAttribute("content", 1),
                new XAttribute("directory", 1),
                new XAttribute("contentChangedAt", 1),
                new XAttribute("hidden", 0));
            foreach (var path in library.Paths)
            {
                directory.Add(new XElement(
                    "Location",
                    new XAttribute("id", locationId++),
                    new XAttribute("pathHash", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant())));
            }

            container.Add(directory);
        }

        return container.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>What plex.tv tells clients about this server (from /api/v2/resources), as raw JSON.</summary>
    public async Task<JsonElement?> GetOwnResourceAsync(CancellationToken ct)
    {
        var c = Config;
        if (string.IsNullOrEmpty(c.ServerToken))
        {
            return null;
        }

        using var request = Request(HttpMethod.Get, "/api/v2/resources?includeHttps=1&includeRelay=1&includeIPv6=1", c.ServerToken);
        request.Headers.Remove("X-Plex-Provides");
        request.Headers.Add("Accept", "application/json");
        using var response = await _httpClientFactory.CreateClient().SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
        foreach (var resource in json.RootElement.EnumerateArray())
        {
            if (resource.TryGetProperty("clientIdentifier", out var id) && id.GetString() == c.MachineIdentifier)
            {
                return resource.Clone();
            }
        }

        return null;
    }

    private async Task<string?> GetPublicAddressAsync(CancellationToken ct)
    {
        try
        {
            var ip = (await _httpClientFactory.CreateClient().GetStringAsync(PlexTv + "/:/ip", ct).ConfigureAwait(false)).Trim();
            return IPAddress.TryParse(ip, out _) ? ip : null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Couldn't look up the public address from plex.tv");
            return null;
        }
    }

    private static IEnumerable<string> LanAddresses()
    {
        var configured = Config.LanAddresses.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList();
        if (configured.Count > 0)
        {
            return configured;
        }

        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .Select(a => a.ToString())
            .Distinct();
    }

    /// <summary>PMS's plex.direct host form: 192.168.1.5 → 192-168-1-5.{hash}.plex.direct.</summary>
    internal static string PlexDirect(string ip, string hash, int port) =>
        string.Create(CultureInfo.InvariantCulture, $"https://{ip.Replace('.', '-').Replace(':', '-')}.{hash}.plex.direct:{port}");
}
