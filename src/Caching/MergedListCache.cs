using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ContinueWatchingPlus.Merging;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContinueWatchingPlus.Caching;

/// <summary>
/// Per-user merged lists. A change to a user's watch state bumps their generation: lists built before it
/// are never served again, and a rebuild runs in the background so the next request is usually answered
/// from memory. Library changes are softer: the current list keeps being served while it is rebuilt,
/// since a newly scanned file rarely changes this row.
/// </summary>
public sealed class MergedListCache : IDisposable
{
    // Library changes are folded into one background rebuild per interval, even during a long scan.
    internal static readonly TimeSpan LibraryRebuildDelay = TimeSpan.FromSeconds(10);

    // Users who loaded the row this recently are kept warm in the background.
    internal static readonly TimeSpan ActiveWindow = TimeSpan.FromHours(24);

    // Lists due to expire within this window are rebuilt ahead of time by RefreshExpiring.
    internal static readonly TimeSpan RefreshLookahead = TimeSpan.FromSeconds(90);

    // During playback, progress for an item not yet in the list invalidates at most this often.
    internal static readonly TimeSpan ProgressInvalidationInterval = TimeSpan.FromMinutes(2);

    private readonly Func<Guid, MergedList> _build;
    private readonly Func<TimeSpan> _timeToLive;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<Guid, Slot> _slots = new();
    private readonly SemaphoreSlim _backgroundGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private long _libraryVersion;
    private int _libraryRebuildScheduled;

    public MergedListCache(Func<Guid, MergedList> build, Func<TimeSpan> timeToLive, TimeProvider timeProvider, ILogger logger)
    {
        _build = build;
        _timeToLive = timeToLive;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public Task<MergedList> GetAsync(Guid userId)
    {
        var slot = _slots.GetOrAdd(userId, _ => new Slot());
        lock (slot.Gate)
        {
            var now = UtcNow;
            slot.LastRequestedUtc = now;

            if (HasValidCurrent(slot) && IsFresh(slot.Current!, now))
            {
                return Task.FromResult(slot.Current!);
            }

            return GetOrStartBuildLocked(userId, slot);
        }
    }

    public void Invalidate(Guid userId, TimeSpan rebuildDelay)
    {
        if (!_slots.TryGetValue(userId, out var slot))
        {
            return;
        }

        lock (slot.Gate)
        {
            InvalidateLocked(userId, slot, rebuildDelay);
        }
    }

    public void InvalidateAll(TimeSpan rebuildDelay)
    {
        foreach (var (userId, slot) in _slots)
        {
            lock (slot.Gate)
            {
                InvalidateLocked(userId, slot, rebuildDelay);
            }
        }
    }

    /// <summary>
    /// Items were added to or removed from the library. Current lists keep being served; active users'
    /// lists are rebuilt in the background once the interval has passed. Removed items already drop out
    /// of responses, since each request looks items up again.
    /// </summary>
    public void MarkLibraryChanged()
    {
        Interlocked.Increment(ref _libraryVersion);
        if (Interlocked.CompareExchange(ref _libraryRebuildScheduled, 1, 0) == 0)
        {
            _ = RebuildForLibraryChangesAsync();
        }
    }

    /// <summary>
    /// Playback progress is reported every few seconds. It only changes the list when the item isn't
    /// already in it as in progress (for example, the first progress report of a new episode).
    /// </summary>
    public void InvalidateForPlaybackProgress(Guid userId, Guid itemId, TimeSpan rebuildDelay)
    {
        if (!_slots.TryGetValue(userId, out var slot))
        {
            return;
        }

        lock (slot.Gate)
        {
            if (HasValidCurrent(slot) && slot.Current!.ContainsInProgress(itemId))
            {
                return;
            }

            var now = UtcNow;
            if (slot.LastProgressItemId.Equals(itemId) && now - slot.LastProgressInvalidationUtc < ProgressInvalidationInterval)
            {
                return;
            }

            slot.LastProgressItemId = itemId;
            slot.LastProgressInvalidationUtc = now;
            InvalidateLocked(userId, slot, rebuildDelay);
        }
    }

    /// <summary>Rebuilds active users' lists shortly before they expire, so requests keep hitting memory.</summary>
    public void RefreshExpiring()
    {
        foreach (var (userId, slot) in _slots)
        {
            lock (slot.Gate)
            {
                var now = UtcNow;
                if (HasValidCurrent(slot)
                    && slot.InFlight is null
                    && !IsFresh(slot.Current!, now + RefreshLookahead))
                {
                    ScheduleRebuildLocked(userId, slot, TimeSpan.Zero);
                }
            }
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
        _backgroundGate.Dispose();
    }

    private static bool HasValidCurrent(Slot slot)
        => slot.Current is not null && slot.CurrentGeneration == slot.Generation;

    // A list is fresh until its time to live runs out or the UTC date changes,
    // since the Next Up cutoff is a whole number of days before today.
    private bool IsFresh(MergedList list, DateTime asOfUtc)
        => asOfUtc - list.BuiltAtUtc < _timeToLive() && asOfUtc.Date == list.BuiltAtUtc.Date;

    private void InvalidateLocked(Guid userId, Slot slot, TimeSpan rebuildDelay)
    {
        slot.Generation++;
        ScheduleRebuildLocked(userId, slot, rebuildDelay);
    }

    private Task<MergedList> GetOrStartBuildLocked(Guid userId, Slot slot)
    {
        if (slot.InFlight is not null && slot.InFlightGeneration == slot.Generation)
        {
            return slot.InFlight;
        }

        var generation = slot.Generation;
        var libraryVersion = Volatile.Read(ref _libraryVersion);
        var completion = new TaskCompletionSource<MergedList>(TaskCreationOptions.RunContinuationsAsynchronously);
        slot.InFlight = completion.Task;
        slot.InFlightGeneration = generation;

        _ = Task.Run(() =>
        {
            try
            {
                var list = _build(userId);
                lock (slot.Gate)
                {
                    // A change during the build makes this list outdated: hand it to the requests
                    // that were already waiting, but don't keep it.
                    if (generation == slot.Generation)
                    {
                        slot.Current = list;
                        slot.CurrentGeneration = generation;
                        slot.CurrentLibraryVersion = libraryVersion;
                    }

                    if (slot.InFlight == completion.Task)
                    {
                        slot.InFlight = null;
                    }
                }

                completion.SetResult(list);
            }
            catch (Exception ex)
            {
                lock (slot.Gate)
                {
                    if (slot.InFlight == completion.Task)
                    {
                        slot.InFlight = null;
                    }
                }

                completion.SetException(ex);
            }
        });

        return completion.Task;
    }

    private void ScheduleRebuildLocked(Guid userId, Slot slot, TimeSpan delay)
    {
        if (UtcNow - slot.LastRequestedUtc > ActiveWindow || _shutdown.IsCancellationRequested)
        {
            return;
        }

        // Debounce: a burst of changes (marking a season played) leads to one rebuild.
        slot.PendingRebuild?.Cancel();
        slot.PendingRebuild?.Dispose();
        var pendingRebuild = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        slot.PendingRebuild = pendingRebuild;

        _ = RebuildAfterDelayAsync(userId, slot, delay, pendingRebuild.Token);
    }

    private async Task RebuildAfterDelayAsync(Guid userId, Slot slot, TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }

            // One background build at a time, so a library scan can't flood the database.
            await _backgroundGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // A second pass covers joining a build that started before the latest change.
                for (var pass = 0; pass < 2; pass++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Task<MergedList> build;
                    lock (slot.Gate)
                    {
                        if (HasValidCurrent(slot)
                            && IsFresh(slot.Current!, UtcNow + RefreshLookahead)
                            && slot.CurrentLibraryVersion == Volatile.Read(ref _libraryVersion))
                        {
                            return;
                        }

                        build = GetOrStartBuildLocked(userId, slot);
                    }

                    await build.ConfigureAwait(false);
                }
            }
            finally
            {
                _backgroundGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ContinueWatchingPlus] Background rebuild failed for user {UserId}", userId);
        }
    }

    private async Task RebuildForLibraryChangesAsync()
    {
        try
        {
            await Task.Delay(LibraryRebuildDelay, _timeProvider, _shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        // Changes from here on schedule the next round, which picks up anything this round misses.
        Volatile.Write(ref _libraryRebuildScheduled, 0);

        var libraryVersion = Volatile.Read(ref _libraryVersion);
        foreach (var (userId, slot) in _slots)
        {
            lock (slot.Gate)
            {
                if (HasValidCurrent(slot) && slot.CurrentLibraryVersion != libraryVersion)
                {
                    ScheduleRebuildLocked(userId, slot, TimeSpan.Zero);
                }
            }
        }
    }

    private sealed class Slot
    {
        public readonly object Gate = new();

        public long Generation;

        public MergedList? Current;

        public long CurrentGeneration = -1;

        public long CurrentLibraryVersion;

        public Task<MergedList>? InFlight;

        public long InFlightGeneration = -1;

        public DateTime LastRequestedUtc;

        public CancellationTokenSource? PendingRebuild;

        public Guid LastProgressItemId;

        public DateTime LastProgressInvalidationUtc;
    }
}
