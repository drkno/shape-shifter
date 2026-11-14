using System.Globalization;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ShapeShifter.Auth;
using Jellyfin.Plugin.ShapeShifter.Plex;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.ShapeShifter.Server;

/// <summary>
/// Per-request view of a Plex call: route values, X-Plex-* parameters (which clients send as
/// either headers or query string) and the authenticated principal.
/// </summary>
public sealed class PlexContext
{
    public PlexContext(HttpContext http, IReadOnlyDictionary<string, string> routeValues)
    {
        Http = http;
        RouteValues = routeValues;
    }

    public HttpContext Http { get; }

    public IReadOnlyDictionary<string, string> RouteValues { get; }

    public PlexPrincipal? Principal { get; set; }

    /// <summary>Gets the Jellyfin user. Only valid on authenticated routes.</summary>
    public User User => Principal?.User ?? throw new InvalidOperationException("Request is not authenticated");

    public string? Token => Param("X-Plex-Token");

    public string ClientIdentifier => Param("X-Plex-Client-Identifier") ?? Http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    public string Product => Param("X-Plex-Product") ?? "Plex Client";

    public string ProductVersion => Param("X-Plex-Version") ?? "0";

    public string DeviceName => Param("X-Plex-Device-Name") ?? Param("X-Plex-Device") ?? Product;

    public string Platform => Param("X-Plex-Platform") ?? "Unknown";

    public bool WantsJson
    {
        get
        {
            var accept = Http.Request.Headers.Accept.ToString();
            return accept.Contains("application/json", StringComparison.OrdinalIgnoreCase);
        }
    }

    public string Route(string name) => RouteValues[name];

    public string? Query(string name)
    {
        var v = Http.Request.Query[name];
        return v.Count > 0 ? v[0] : null;
    }

    public int? QueryInt(string name) =>
        int.TryParse(Query(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    public long? QueryLong(string name) =>
        long.TryParse(Query(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    public bool QueryBool(string name) => Query(name) is "1" or "true";

    /// <summary>Reads an X-Plex-* style value from the header or the query string.</summary>
    public string? Param(string name)
    {
        var header = Http.Request.Headers[name];
        if (header.Count > 0 && !string.IsNullOrEmpty(header[0]))
        {
            return header[0];
        }

        return Query(name);
    }

    public (int Start, int? Size) Paging()
    {
        var start = int.TryParse(Param("X-Plex-Container-Start"), out var s) ? Math.Max(0, s) : 0;
        int? size = int.TryParse(Param("X-Plex-Container-Size"), out var z) ? Math.Max(0, z) : null;
        return (start, size);
    }

    public Task Write(PlexNode container, int statusCode = 200)
    {
        Http.Response.StatusCode = statusCode;
        if (WantsJson)
        {
            Http.Response.ContentType = "application/json";
            return PlexSerializer.WriteJsonAsync(Http.Response.Body, container);
        }

        Http.Response.ContentType = "text/xml;charset=utf-8";
        return PlexSerializer.WriteXmlAsync(Http.Response.Body, container);
    }

    public Task WriteEmpty() => Write(PlexNode.Container().Set("size", 0));

    public Task Status(int statusCode)
    {
        Http.Response.StatusCode = statusCode;
        return Task.CompletedTask;
    }
}
