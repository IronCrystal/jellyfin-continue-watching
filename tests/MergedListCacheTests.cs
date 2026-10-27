using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ContinueWatchingPlus.Caching;
using Jellyfin.Plugin.ContinueWatchingPlus.Merging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.ContinueWatchingPlus.Tests;

public sealed class MergedListCacheTests : IDisposable
{
    private static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RebuildDelay = TimeSpan.FromMilliseconds(500);

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _inProgressItemId = Guid.NewGuid();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
    private readonly ManualResetEventSlim _buildGate = new(initialState: true);
    private readonly MergedListCache _cache;
    private int _buildCount;
    private bool _failNextBuild;

    public MergedListCacheTests()
    {
        _cache = new MergedListCache(Build, () => TimeToLive, _time, NullLogger.Instance);
    }

    public void Dispose()
    {
        _buildGate.Set();
        _cache.Dispose();
        _buildGate.Dispose();
    }

    private int BuildCount => Volatile.Read(ref _buildCount);

    private MergedList Build(Guid userId)
    {
        Interlocked.Increment(ref _buildCount);
        _buildGate.Wait(TimeSpan.FromSeconds(10));
        if (_failNextBuild)
        {
            _failNextBuild = false;
            throw new InvalidOperationException("database unavailable");
        }

        return new MergedList(
            new[] { new MergedEntry(_inProgressItemId, BaseItemKind.Episode, false, _time.GetUtcNow().UtcDateTime) },
            _time.GetUtcNow().UtcDateTime);
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), "Timed out waiting for condition");
            Thread.Sleep(5);
        }
    }

    // Lets fire-and-forget work queued by the cache run, so "nothing happened" assertions are meaningful.
    private static void Settle() => Thread.Sleep(100);

    [Fact]
    public async Task SecondRequest_IsServedFromMemory()
    {
        var first = await _cache.GetAsync(_userId);
        var second = await _cache.GetAsync(_userId);

        Assert.Same(first, second);
        Assert.Equal(1, BuildCount);
    }

    [Fact]
    public async Task AfterInvalidation_TheNextRequestNeverGetsTheOldList()
    {
        var before = await _cache.GetAsync(_userId);

        _cache.Invalidate(_userId, TimeSpan.FromHours(1));
        var after = await _cache.GetAsync(_userId);

        Assert.NotSame(before, after);
    }

    [Fact]
    public async Task AfterInvalidation_TheListIsRebuiltInTheBackground()
    {
        await _cache.GetAsync(_userId);

        _cache.Invalidate(_userId, RebuildDelay);
        _time.Advance(RebuildDelay);
        WaitUntil(() => BuildCount == 2);
        Settle();

        await _cache.GetAsync(_userId);
        Assert.Equal(2, BuildCount);
    }

    [Fact]
    public async Task ABurstOfChanges_CausesOneBackgroundRebuild()
    {
        await _cache.GetAsync(_userId);

        for (var i = 0; i < 20; i++)
        {
            _cache.Invalidate(_userId, RebuildDelay);
            _time.Advance(TimeSpan.FromMilliseconds(100));
        }

        _time.Advance(RebuildDelay);
        WaitUntil(() => BuildCount == 2);
        Settle();
        Assert.Equal(2, BuildCount);
    }

    [Fact]
    public async Task AChangeDuringABuild_DiscardsThatBuildsResult()
    {
        _buildGate.Reset();
        var duringChange = _cache.GetAsync(_userId);
        WaitUntil(() => BuildCount == 1);

        _cache.Invalidate(_userId, TimeSpan.FromHours(1));
        var afterChange = _cache.GetAsync(_userId);
        WaitUntil(() => BuildCount == 2);
        _buildGate.Set();

        var stale = await duringChange;
        var fresh = await afterChange;
        Assert.NotSame(stale, fresh);
        Assert.Same(fresh, await _cache.GetAsync(_userId));
        Assert.Equal(2, BuildCount);
    }

    [Fact]
    public async Task ConcurrentRequests_ShareOneBuild()
    {
        _buildGate.Reset();
        var first = _cache.GetAsync(_userId);
        var second = _cache.GetAsync(_userId);
        _buildGate.Set();

        Assert.Same(await first, await second);
        Assert.Equal(1, BuildCount);
    }

    [Fact]
    public async Task AnExpiredList_IsRebuilt()
    {
        var first = await _cache.GetAsync(_userId);

        _time.Advance(TimeToLive);

        Assert.NotSame(first, await _cache.GetAsync(_userId));
    }

    [Fact]
    public async Task AListFromYesterday_IsRebuilt()
    {
        _time.SetUtcNow(new DateTimeOffset(2026, 10, 27, 23, 59, 30, TimeSpan.Zero));
        var first = await _cache.GetAsync(_userId);

        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.NotSame(first, await _cache.GetAsync(_userId));
    }

    [Fact]
    public async Task PlaybackProgress_ForAnItemAlreadyInProgress_KeepsTheList()
    {
        var first = await _cache.GetAsync(_userId);

        _cache.InvalidateForPlaybackProgress(_userId, _inProgressItemId, TimeSpan.FromHours(1));

        Assert.Same(first, await _cache.GetAsync(_userId));
    }

    [Fact]
    public async Task PlaybackProgress_ForANewItem_InvalidatesOnceThenThrottles()
    {
        var newItemId = Guid.NewGuid();
        var first = await _cache.GetAsync(_userId);

        _cache.InvalidateForPlaybackProgress(_userId, newItemId, TimeSpan.FromHours(1));
        var second = await _cache.GetAsync(_userId);
        Assert.NotSame(first, second);

        // The rebuilt list still doesn't contain the item (say it's in an excluded library);
        // progress reports every few seconds must not rebuild every time.
        _time.Advance(TimeSpan.FromSeconds(10));
        _cache.InvalidateForPlaybackProgress(_userId, newItemId, TimeSpan.FromHours(1));
        Assert.Same(second, await _cache.GetAsync(_userId));
    }

    [Fact]
    public async Task InactiveUsers_AreNotRebuiltInTheBackground()
    {
        await _cache.GetAsync(_userId);
        _time.Advance(MergedListCache.ActiveWindow + TimeSpan.FromMinutes(1));

        _cache.Invalidate(_userId, RebuildDelay);
        _time.Advance(RebuildDelay);
        Settle();

        Assert.Equal(1, BuildCount);
    }

    [Fact]
    public async Task ListsAboutToExpire_AreRefreshedWhileTheOldListIsStillServed()
    {
        var original = await _cache.GetAsync(_userId);
        _time.Advance(TimeToLive - MergedListCache.RefreshLookahead + TimeSpan.FromSeconds(1));

        _buildGate.Reset();
        _cache.RefreshExpiring();
        WaitUntil(() => BuildCount == 2);

        var duringRefresh = _cache.GetAsync(_userId);
        Assert.True(duringRefresh.IsCompleted);
        Assert.Same(original, await duringRefresh);

        _buildGate.Set();
        WaitUntil(() => !ReferenceEquals(original, _cache.GetAsync(_userId).Result));
        Assert.Equal(2, BuildCount);
    }

    [Fact]
    public async Task AFailedBuild_IsRetriedOnTheNextRequest()
    {
        _failNextBuild = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _cache.GetAsync(_userId));

        await _cache.GetAsync(_userId);
        Assert.Equal(2, BuildCount);
    }

    [Fact]
    public void InvalidatingAUserWithNoList_DoesNothing()
    {
        _cache.Invalidate(Guid.NewGuid(), TimeSpan.Zero);
        _cache.InvalidateAll(TimeSpan.Zero);
        Settle();

        Assert.Equal(0, BuildCount);
    }
}
