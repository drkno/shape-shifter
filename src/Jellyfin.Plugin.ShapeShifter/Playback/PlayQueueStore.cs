using System.Collections.Concurrent;

namespace Jellyfin.Plugin.ShapeShifter.Playback;

public sealed class PlayQueue
{
    public required long Id { get; init; }

    public required Guid UserId { get; init; }

    public required string SourceUri { get; init; }

    /// <summary>Gets the queued items as (playQueueItemID, Jellyfin item id).</summary>
    public List<(long ItemId, Guid JellyfinId)> Items { get; } = [];

    public int SelectedIndex { get; set; }

    public bool Shuffled { get; init; }

    public int Version { get; set; } = 1;

    public DateTime LastAccess { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// In-memory play queues. Modern Plex clients always play through a play queue (POST /playQueues)
/// rather than an item directly. Queues only need to outlive a viewing session.
/// </summary>
public sealed class PlayQueueStore
{
    private static readonly TimeSpan Expiry = TimeSpan.FromDays(2);

    private readonly ConcurrentDictionary<long, PlayQueue> _queues = new();
    private long _nextQueueId = 1;
    private long _nextItemId = 1;

    public PlayQueue Create(Guid userId, string sourceUri, IEnumerable<Guid> items, int selectedIndex, bool shuffled)
    {
        Prune();
        var queue = new PlayQueue
        {
            Id = Interlocked.Increment(ref _nextQueueId),
            UserId = userId,
            SourceUri = sourceUri,
            Shuffled = shuffled,
        };
        foreach (var item in items)
        {
            queue.Items.Add((Interlocked.Increment(ref _nextItemId), item));
        }

        queue.SelectedIndex = Math.Clamp(selectedIndex, 0, Math.Max(0, queue.Items.Count - 1));
        _queues[queue.Id] = queue;
        return queue;
    }

    public PlayQueue? Get(long id, Guid userId)
    {
        if (_queues.TryGetValue(id, out var queue) && queue.UserId == userId)
        {
            queue.LastAccess = DateTime.UtcNow;
            return queue;
        }

        return null;
    }

    public long NextItemId() => Interlocked.Increment(ref _nextItemId);

    public void Remove(long id) => _queues.TryRemove(id, out _);

    private void Prune()
    {
        var cutoff = DateTime.UtcNow - Expiry;
        foreach (var (id, queue) in _queues)
        {
            if (queue.LastAccess < cutoff)
            {
                _queues.TryRemove(id, out _);
            }
        }
    }
}
