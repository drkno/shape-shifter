using System.Collections.Concurrent;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShapeShifter.Data;

/// <summary>
/// Persistent mapping between Plex's integer ids and Jellyfin identifiers.
/// Kinds: section, item, part, stream, account. The Jellyfin side is an opaque string
/// (a Guid for items/sections, "itemGuid|mediaSourceId" for parts, "...|index" for streams).
/// </summary>
public sealed class IdMap : IDisposable
{
    public const string Section = "section";
    public const string Item = "item";
    public const string Part = "part";
    public const string Stream = "stream";
    public const string PlaylistItem = "playlistitem";

    // New ids start high so ids seeded from a PMS database (which are far smaller) never collide.
    private const long FirstAllocatedId = 50_000_000;

    private readonly SqliteConnection _connection;
    private readonly ConcurrentDictionary<(string Kind, long PlexId), string> _toJellyfin = new();
    private readonly ConcurrentDictionary<(string Kind, string JellyfinId), long> _toPlex = new();
    private readonly Lock _writeLock = new();
    private readonly ILogger<IdMap> _logger;
    private long _next;

    public IdMap(IApplicationPaths paths, ILogger<IdMap> logger)
    {
        _logger = logger;
        var dir = Path.Combine(paths.PluginConfigurationsPath, "ShapeShifter");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "idmap.db");

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        Execute("PRAGMA journal_mode=WAL;");
        Execute("""
            CREATE TABLE IF NOT EXISTS ids (
                kind TEXT NOT NULL,
                plex_id INTEGER NOT NULL,
                jf_id TEXT NOT NULL,
                PRIMARY KEY (kind, plex_id),
                UNIQUE (kind, jf_id)
            );
            """);

        Load();
    }

    public long GetOrAdd(string kind, string jellyfinId)
    {
        if (_toPlex.TryGetValue((kind, jellyfinId), out var existing))
        {
            return existing;
        }

        lock (_writeLock)
        {
            if (_toPlex.TryGetValue((kind, jellyfinId), out existing))
            {
                return existing;
            }

            var id = _next++;
            Insert(kind, id, jellyfinId);
            return id;
        }
    }

    public long GetOrAdd(string kind, Guid jellyfinId) => GetOrAdd(kind, jellyfinId.ToString("N"));

    public string? Resolve(string kind, long plexId) =>
        _toJellyfin.TryGetValue((kind, plexId), out var value) ? value : null;

    public Guid? ResolveGuid(string kind, long plexId) =>
        Resolve(kind, plexId) is { } s && Guid.TryParse(s, out var g) ? g : null;

    public Guid? ResolveGuid(string kind, string? plexId) =>
        long.TryParse(plexId, out var id) ? ResolveGuid(kind, id) : null;

    /// <summary>Seeds a mapping with a known Plex id (used when importing from PMS). Existing mappings win.</summary>
    public bool TrySeed(string kind, long plexId, string jellyfinId)
    {
        lock (_writeLock)
        {
            if (_toJellyfin.ContainsKey((kind, plexId)) || _toPlex.ContainsKey((kind, jellyfinId)))
            {
                return false;
            }

            Insert(kind, plexId, jellyfinId);
            return true;
        }
    }

    public void Dispose() => _connection.Dispose();

    private void Insert(string kind, long plexId, string jellyfinId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT INTO ids (kind, plex_id, jf_id) VALUES ($k, $p, $j)";
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$p", plexId);
        cmd.Parameters.AddWithValue("$j", jellyfinId);
        cmd.ExecuteNonQuery();
        _toJellyfin[(kind, plexId)] = jellyfinId;
        _toPlex[(kind, jellyfinId)] = plexId;
    }

    private void Load()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT kind, plex_id, jf_id FROM ids";
        using var reader = cmd.ExecuteReader();
        long max = FirstAllocatedId - 1;
        var count = 0;
        while (reader.Read())
        {
            var kind = reader.GetString(0);
            var plexId = reader.GetInt64(1);
            var jfId = reader.GetString(2);
            _toJellyfin[(kind, plexId)] = jfId;
            _toPlex[(kind, jfId)] = plexId;
            max = Math.Max(max, plexId);
            count++;
        }

        _next = max + 1;
        _logger.LogInformation("Loaded {Count} Plex id mappings", count);
    }

    private void Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
