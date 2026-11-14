using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.ShapeShifter.Server;

/// <summary>
/// Minimal router. ASP.NET route templates don't cope well with Plex paths such as
/// "/:/timeline" or "/video/:/transcode/universal/start.m3u8", so patterns are compiled to
/// regexes: "{name}" matches one segment, "{*name}" matches the rest of the path.
/// </summary>
public sealed class PlexRouter
{
    private readonly List<RouteEntry> _routes = [];

    public interface IRouteBuilder
    {
        IRouteBuilder Get(string pattern, Func<PlexContext, Task> handler, bool anonymous = false);

        IRouteBuilder Post(string pattern, Func<PlexContext, Task> handler);

        IRouteBuilder Put(string pattern, Func<PlexContext, Task> handler);

        IRouteBuilder Delete(string pattern, Func<PlexContext, Task> handler);

        IRouteBuilder Any(string pattern, Func<PlexContext, Task> handler, bool anonymous = false);
    }

    public IRouteBuilder Builder => new RouteBuilder(this);

    public RouteMatch? Match(string method, string path)
    {
        if (method == "HEAD")
        {
            method = "GET";
        }

        foreach (var route in _routes)
        {
            if (route.Method != "*" && route.Method != method)
            {
                continue;
            }

            var m = route.Regex.Match(path);
            if (!m.Success)
            {
                continue;
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in route.Names)
            {
                values[name] = Uri.UnescapeDataString(m.Groups[name].Value);
            }

            return new RouteMatch(route, values);
        }

        return null;
    }

    private void Add(string method, string pattern, Func<PlexContext, Task> handler, bool anonymous)
    {
        var names = new List<string>();
        // Regex.Escape turns "{*rest}" into "\{\*rest}".
        var regex = "^" + Regex.Replace(Regex.Escape(pattern), @"\\\{(\\\*)?(\w+)\}", m =>
        {
            names.Add(m.Groups[2].Value);
            return m.Groups[1].Success ? $"(?<{m.Groups[2].Value}>.*)" : $"(?<{m.Groups[2].Value}>[^/]+)";
        }) + "/?$";

        _routes.Add(new RouteEntry(method, new Regex(regex, RegexOptions.Compiled | RegexOptions.CultureInvariant), names, handler, anonymous));
    }

    public sealed record RouteEntry(string Method, Regex Regex, IReadOnlyList<string> Names, Func<PlexContext, Task> Handler, bool Anonymous);

    public sealed record RouteMatch(RouteEntry Route, IReadOnlyDictionary<string, string> Values);

    private sealed class RouteBuilder(PlexRouter router) : IRouteBuilder
    {
        public IRouteBuilder Get(string pattern, Func<PlexContext, Task> handler, bool anonymous = false) => Add("GET", pattern, handler, anonymous);

        public IRouteBuilder Post(string pattern, Func<PlexContext, Task> handler) => Add("POST", pattern, handler, false);

        public IRouteBuilder Put(string pattern, Func<PlexContext, Task> handler) => Add("PUT", pattern, handler, false);

        public IRouteBuilder Delete(string pattern, Func<PlexContext, Task> handler) => Add("DELETE", pattern, handler, false);

        public IRouteBuilder Any(string pattern, Func<PlexContext, Task> handler, bool anonymous = false) => Add("*", pattern, handler, anonymous);

        private RouteBuilder Add(string method, string pattern, Func<PlexContext, Task> handler, bool anonymous)
        {
            router.Add(method, pattern, handler, anonymous);
            return this;
        }
    }
}

/// <summary>A group of Plex endpoints registered with the router.</summary>
public interface IPlexEndpoints
{
    void Map(PlexRouter.IRouteBuilder routes);
}
