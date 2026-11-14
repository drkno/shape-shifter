using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ShapeShifter.Auth;
using Jellyfin.Plugin.ShapeShifter.Configuration;
using Jellyfin.Plugin.ShapeShifter.Data;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShapeShifter.Import;

/// <summary>
/// Takes over an existing Plex Media Server: its identity (so plex.tv and every client keep
/// seeing "the same server"), its plex.direct certificate, its ids (so cached ratingKeys,
/// playlists and deep links still resolve) and per-account watch state.
/// PMS's data directory is only read, never modified.
/// </summary>
public sealed class PmsImportTask : IScheduledTask
{
    private const string LibraryDb = "Plug-in Support/Databases/com.plexapp.plugins.library.db";

    // PMS stores the owner as local account 1 in its database.
    private const long PmsOwnerAccountId = 1;

    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IApplicationPaths _paths;
    private readonly IdMap _ids;
    private readonly PlexAuthService _auth;
    private readonly ILogger<PmsImportTask> _logger;

    public PmsImportTask(ILibraryManager libraryManager, IUserDataManager userDataManager, IApplicationPaths paths, IdMap ids, PlexAuthService auth, ILogger<PmsImportTask> logger)
    {
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _paths = paths;
        _ids = ids;
        _auth = auth;
        _logger = logger;
    }

    public string Name => "Import from Plex Media Server";

    public string Key => "ShapeShifterPmsImport";

    public string Description => "Imports library ids and watch history from PMS (safe while PMS runs). With 'take over identity' enabled it also imports PMS's identity and certificates; stop PMS first.";

    public string Category => Plugin.PluginName;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance!;
        var config = plugin.Configuration;
        var dataDir = config.PmsDataPath;
        if (string.IsNullOrEmpty(dataDir) || !Directory.Exists(dataDir))
        {
            throw new InvalidOperationException($"PMS data path '{dataDir}' not found. Set it to the folder containing Preferences.xml.");
        }

        if (config.ImportIdentity)
        {
            ImportIdentity(dataDir);
        }
        else
        {
            _logger.LogInformation("Data-only import: keeping this server's own identity ({MachineId})", config.MachineIdentifier);
        }

        progress.Report(5);

        var dbPath = Path.Combine(dataDir, LibraryDb);
        if (!File.Exists(dbPath))
        {
            _logger.LogWarning("PMS library database not found at {Path}; skipping library import", dbPath);
            plugin.SaveConfiguration();
            return;
        }

        // Work on a snapshot: PMS may still be running (side-by-side) and we must never write to its files.
        var snapshot = Path.Combine(Path.GetTempPath(), $"shapeshifter-pms-{Guid.NewGuid():N}.db");
        try
        {
            Snapshot(dbPath, snapshot);
            await using var db = new SqliteConnection($"Data Source={snapshot};Mode=ReadOnly");
            await db.OpenAsync(cancellationToken).ConfigureAwait(false);

            ImportSections(db);
            progress.Report(10);
            var byGuid = ImportItems(db, progress, cancellationToken);
            progress.Report(70);
            ImportWatchState(db, byGuid, progress, cancellationToken);
            progress.Report(100);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { snapshot, snapshot + "-wal", snapshot + "-shm" })
            {
                File.Delete(f);
            }
        }

        plugin.SaveConfiguration();
    }

    /// <summary>
    /// Copies the PMS database consistently. SQLite's online backup works while PMS is writing;
    /// a plain file copy (database + WAL) is the fallback if the source can't be opened.
    /// </summary>
    private void Snapshot(string dbPath, string snapshot)
    {
        try
        {
            using var source = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            using var dest = new SqliteConnection($"Data Source={snapshot}");
            source.Open();
            dest.Open();
            source.BackupDatabase(dest);
            return;
        }
        catch (SqliteException ex)
        {
            _logger.LogInformation(ex, "Online backup of the PMS database failed; copying files instead");
        }

        File.Copy(dbPath, snapshot, overwrite: true);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(dbPath + suffix))
            {
                File.Copy(dbPath + suffix, snapshot + suffix, overwrite: true);
            }
        }
    }

    private void ImportIdentity(string dataDir)
    {
        var config = Plugin.Instance!.Configuration;
        var prefsPath = Path.Combine(dataDir, "Preferences.xml");
        if (!File.Exists(prefsPath))
        {
            throw new InvalidOperationException($"Preferences.xml not found in {dataDir}");
        }

        var prefs = XDocument.Load(prefsPath).Root!;
        string? Pref(string name) => (string?)prefs.Attribute(name) is { Length: > 0 } v ? v : null;

        // ProcessedMachineIdentifier is what /identity and plex.tv's clientIdentifier show.
        var processedId = Pref("ProcessedMachineIdentifier") ?? throw new InvalidOperationException("Preferences.xml has no ProcessedMachineIdentifier");
        config.Mode = IdentityMode.Takeover;
        config.MachineIdentifier = processedId;
        config.ServerToken = Pref("PlexOnlineToken") ?? config.ServerToken;
        config.OwnerPlexUsername = Pref("PlexOnlineUsername") ?? config.OwnerPlexUsername;
        config.FriendlyName = Pref("FriendlyName") ?? config.FriendlyName;
        config.OwnerPlexAccountId = string.Empty; // re-resolved from the server token on first use
        if (int.TryParse(Pref("secureConnections"), out var secure) && Enum.IsDefined(typeof(SecureConnections), secure))
        {
            config.SecureConnections = (SecureConnections)secure;
        }

        if (Pref("ManualPortMappingMode") == "1" && int.TryParse(Pref("ManualPortMappingPort"), out var publicPort))
        {
            config.PublicPort = publicPort;
        }

        if (Pref("customConnections") is { } custom)
        {
            config.CustomConnections = custom.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        // Publishing stays off until the connection preview has been checked on the status page.
        config.PublishConnections = false;
        _logger.LogInformation("Imported PMS identity {Name} ({MachineId}) owned by {Owner}", config.FriendlyName, config.MachineIdentifier, config.OwnerPlexUsername);

        // PMS protects its plex.direct certificate with sha512("plex" + ProcessedMachineIdentifier).
        var plexDirectPassword = Convert.ToHexString(SHA512.HashData(Encoding.UTF8.GetBytes("plex" + processedId))).ToLowerInvariant();
        if (ImportCertificate(Path.Combine(dataDir, "Cache", "cert-v2.p12"), plexDirectPassword, "plex-direct.p12", "plex.direct") is { } plexDirect)
        {
            config.CertificatePath = plexDirect;
            config.CertificatePassword = plexDirectPassword;
        }

        if (Pref("customCertificatePath") is { } customPath
            && ImportCertificate(RewritePath(customPath), Pref("customCertificateKey") ?? string.Empty, "custom.p12", "custom") is { } customCert)
        {
            config.CustomCertificatePath = customCert;
            config.CustomCertificatePassword = Pref("customCertificateKey") ?? string.Empty;
            config.CustomCertificateDomain = Pref("customCertificateDomain") ?? string.Empty;
        }
    }

    /// <summary>Verifies a certificate opens with the password and copies it into our config dir.</summary>
    private string? ImportCertificate(string source, string password, string fileName, string what)
    {
        if (!File.Exists(source))
        {
            _logger.LogInformation("No {What} certificate at {Path}", what, source);
            return null;
        }

        try
        {
            using var cert = X509CertificateLoader.LoadPkcs12FromFile(source, password);
            var dest = Path.Combine(_paths.PluginConfigurationsPath, "ShapeShifter", fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(source, dest, overwrite: true);
            _logger.LogInformation("Imported {What} certificate {Subject}, valid until {Expiry:yyyy-MM-dd}", what, cert.Subject, cert.NotAfter);
            return dest;
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "Couldn't open the PMS {What} certificate {Path}; import it manually", what, source);
            return null;
        }
    }

    private string RewritePath(string pmsPath)
    {
        foreach (var rule in Plugin.Instance!.Configuration.PathRewrites)
        {
            var parts = rule.Split("=>", 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && pmsPath.StartsWith(parts[0], StringComparison.Ordinal))
            {
                return parts[1] + pmsPath[parts[0].Length..];
            }
        }

        return pmsPath;
    }

    private void ImportSections(SqliteConnection db)
    {
        var folders = _libraryManager.GetUserRootFolder().Children.OfType<CollectionFolder>().ToList();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT ls.id, ls.name, sl.root_path FROM library_sections ls JOIN section_locations sl ON sl.library_section_id = ls.id";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var root = RewritePath(reader.GetString(2)).TrimEnd('/', '\\');
            var folder = folders.FirstOrDefault(f => f.PhysicalLocations.Any(p => string.Equals(p.TrimEnd('/', '\\'), root, StringComparison.Ordinal)));
            if (folder is null)
            {
                _logger.LogWarning("PMS section '{Name}' ({Root}) has no matching Jellyfin library", reader.GetString(1), root);
            }
            else if (_ids.TrySeed(IdMap.Section, id, folder.Id.ToString("N")))
            {
                _logger.LogInformation("Mapped PMS section '{Name}' (id {Id}) to Jellyfin library '{Folder}'", reader.GetString(1), id, folder.Name);
            }
        }
    }

    /// <summary>Seeds item/part ids by file path and returns PMS guid → Jellyfin leaf items.</summary>
    private Dictionary<string, List<BaseItem>> ImportItems(SqliteConnection db, IProgress<double> progress, CancellationToken ct)
    {
        using var count = db.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM media_parts";
        var total = Math.Max(1, Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture));

        // metadata_items: id, parent_id, guid; for episodes parent = season, grandparent = show.
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT mi.id, mi.guid, mp.id, mp.file, p.id, p.parent_id
            FROM media_parts mp
            JOIN media_items m ON m.id = mp.media_item_id
            JOIN metadata_items mi ON mi.id = m.metadata_item_id
            LEFT JOIN metadata_items p ON p.id = mi.parent_id
            WHERE mp.file IS NOT NULL AND mp.file <> ''
            """;

        var byGuid = new Dictionary<string, List<BaseItem>>(StringComparer.Ordinal);
        long seen = 0, matched = 0;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (++seen % 500 == 0)
            {
                progress.Report(10 + (60.0 * seen / total));
            }

            var file = RewritePath(reader.GetString(3));
            if (_libraryManager.FindByPath(file, false) is not { } item)
            {
                continue;
            }

            matched++;
            _ids.TrySeed(IdMap.Item, reader.GetInt64(0), item.Id.ToString("N"));
            _ids.TrySeed(IdMap.Part, reader.GetInt64(2), $"{item.Id:N}|{item.Id:N}");

            BaseItem? parent = item switch { Episode e => e.Season, Audio a => a.AlbumEntity, _ => null };
            BaseItem? grandparent = item switch
            {
                Episode e => e.Series,
                Audio a when a.AlbumArtists.Count > 0 => _libraryManager.GetArtist(a.AlbumArtists[0]),
                _ => null,
            };
            if (parent is not null && !reader.IsDBNull(4))
            {
                _ids.TrySeed(IdMap.Item, reader.GetInt64(4), parent.Id.ToString("N"));
            }

            if (grandparent is not null && !reader.IsDBNull(5))
            {
                _ids.TrySeed(IdMap.Item, reader.GetInt64(5), grandparent.Id.ToString("N"));
            }

            if (!reader.IsDBNull(1))
            {
                var guid = reader.GetString(1);
                if (!byGuid.TryGetValue(guid, out var list))
                {
                    byGuid[guid] = list = [];
                }

                list.Add(item);
            }
        }

        _logger.LogInformation("Matched {Matched} of {Seen} PMS media files to Jellyfin items", matched, seen);
        return byGuid;
    }

    private void ImportWatchState(SqliteConnection db, Dictionary<string, List<BaseItem>> byGuid, IProgress<double> progress, CancellationToken ct)
    {
        var accounts = new Dictionary<long, string>();
        using (var acc = db.CreateCommand())
        {
            acc.CommandText = "SELECT id, name FROM accounts";
            using var r = acc.ExecuteReader();
            while (r.Read())
            {
                accounts[r.GetInt64(0)] = r.IsDBNull(1) ? string.Empty : r.GetString(1);
            }
        }

        var users = new Dictionary<long, User?>();
        User? UserFor(long accountId)
        {
            if (!users.TryGetValue(accountId, out var user))
            {
                var isOwner = accountId == PmsOwnerAccountId;
                var config = Plugin.Instance!.Configuration;
                var principal = _auth.MapAccount(
                    isOwner ? config.OwnerPlexAccountId : accountId.ToString(CultureInfo.InvariantCulture),
                    isOwner ? config.OwnerPlexUsername : accounts.GetValueOrDefault(accountId, string.Empty),
                    isOwner);
                users[accountId] = user = principal?.User;
                if (user is null)
                {
                    _logger.LogWarning("Skipping watch history for PMS account {Name} ({Id}): no Jellyfin user mapped", accounts.GetValueOrDefault(accountId), accountId);
                }
            }

            return user;
        }

        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT account_id, guid, view_offset, view_count, last_viewed_at, rating FROM metadata_item_settings WHERE guid IS NOT NULL";
        using var reader = cmd.ExecuteReader();
        long applied = 0;
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (!byGuid.TryGetValue(reader.GetString(1), out var items) || UserFor(reader.GetInt64(0)) is not { } user)
            {
                continue;
            }

            var offsetMs = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
            var viewCount = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
            DateTime? lastViewed = reader.IsDBNull(4) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4)).UtcDateTime;
            double? rating = reader.IsDBNull(5) ? null : reader.GetDouble(5);

            foreach (var item in items)
            {
                if (Apply(user, item, offsetMs, viewCount, lastViewed, rating, ct))
                {
                    applied++;
                }
            }
        }

        _logger.LogInformation("Applied {Count} PMS watch-state entries", applied);
    }

    /// <summary>Merges PMS state into Jellyfin without ever losing Jellyfin-side progress.</summary>
    private bool Apply(User user, BaseItem item, long offsetMs, int viewCount, DateTime? lastViewed, double? rating, CancellationToken ct)
    {
        var data = _userDataManager.GetUserData(user, item);
        if (data is null)
        {
            return false;
        }

        var changed = false;
        if (viewCount > 0 && (!data.Played || data.PlayCount < viewCount))
        {
            data.Played = true;
            data.PlayCount = Math.Max(data.PlayCount, viewCount);
            changed = true;
        }

        if (lastViewed is not null && (data.LastPlayedDate is null || data.LastPlayedDate < lastViewed))
        {
            data.LastPlayedDate = lastViewed;
            changed = true;
        }

        if (offsetMs > 0 && data.PlaybackPositionTicks == 0 && (lastViewed is null || data.LastPlayedDate <= lastViewed))
        {
            data.PlaybackPositionTicks = offsetMs * TimeSpan.TicksPerMillisecond;
            changed = true;
        }

        if (rating is not null && data.Rating is null)
        {
            data.Rating = rating;
            changed = true;
        }

        if (changed)
        {
            _userDataManager.SaveUserData(user, item, data, UserDataSaveReason.Import, ct);
        }

        return changed;
    }
}
