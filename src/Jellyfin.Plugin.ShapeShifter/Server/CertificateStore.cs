using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Jellyfin.Plugin.ShapeShifter.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShapeShifter.Server;

/// <summary>
/// The certificates the listener presents: PMS's *.{hash}.plex.direct certificate and/or a
/// custom one for custom access URLs. Chosen per connection by SNI host name.
/// </summary>
public sealed class CertificateStore : IDisposable
{
    private readonly List<X509Certificate2> _certificates = [];

    private CertificateStore()
    {
    }

    public X509Certificate2? PlexDirect { get; private set; }

    public X509Certificate2? Custom { get; private set; }

    /// <summary>Gets the plex.direct hash, from the certificate's "*.{hash}.plex.direct" name.</summary>
    public string? PlexDirectHash { get; private set; }

    public bool Any => _certificates.Count > 0;

    public static CertificateStore Load(PluginConfiguration config, ILogger logger)
    {
        var store = new CertificateStore();
        store.PlexDirect = TryLoad(config.CertificatePath, config.CertificatePassword, "plex.direct", logger);
        store.Custom = TryLoad(config.CustomCertificatePath, config.CustomCertificatePassword, "custom", logger);
        if (store.PlexDirect is not null)
        {
            store._certificates.Add(store.PlexDirect);
            store.PlexDirectHash = DnsNames(store.PlexDirect)
                .Select(n => n.Split('.'))
                .FirstOrDefault(p => p.Length == 4 && p[0] == "*" && p[2] == "plex" && p[3] == "direct")?[1];
        }

        if (store.Custom is not null)
        {
            store._certificates.Add(store.Custom);
        }

        return store;
    }

    /// <summary>Picks the certificate whose names match the SNI host; falls back to the first loaded.</summary>
    public X509Certificate2? Select(string? hostName)
    {
        if (!string.IsNullOrEmpty(hostName))
        {
            foreach (var cert in _certificates)
            {
                if (DnsNames(cert).Any(n => Matches(n, hostName)))
                {
                    return cert;
                }
            }
        }

        return _certificates.FirstOrDefault();
    }

    public void Dispose()
    {
        foreach (var cert in _certificates)
        {
            cert.Dispose();
        }
    }

    internal static bool Matches(string pattern, string host)
    {
        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            var dot = host.IndexOf('.', StringComparison.Ordinal);
            return dot > 0 && string.Equals(host[(dot + 1)..], pattern[2..], StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(pattern, host, StringComparison.OrdinalIgnoreCase);
    }

    internal static IEnumerable<string> DnsNames(X509Certificate2 cert)
    {
        var san = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (san is not null)
        {
            foreach (var name in san.EnumerateDnsNames())
            {
                yield return name;
            }
        }

        var cn = cert.GetNameInfo(X509NameType.DnsName, false);
        if (!string.IsNullOrEmpty(cn))
        {
            yield return cn;
        }
    }

    private static X509Certificate2? TryLoad(string path, string password, string what, ILogger logger)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        if (!File.Exists(path))
        {
            logger.LogWarning("The {What} certificate {Path} doesn't exist", what, path);
            return null;
        }

        try
        {
            var cert = X509CertificateLoader.LoadPkcs12FromFile(path, password);
            var level = cert.NotAfter < DateTime.Now.AddDays(14) ? LogLevel.Warning : LogLevel.Information;
            logger.Log(level, "Loaded {What} certificate {Subject}, expires {Expiry:yyyy-MM-dd}", what, cert.Subject, cert.NotAfter);
            return cert;
        }
        catch (CryptographicException ex)
        {
            logger.LogError(ex, "Couldn't open the {What} certificate {Path}", what, path);
            return null;
        }
    }
}
