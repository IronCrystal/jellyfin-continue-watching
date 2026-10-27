using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContinueWatchingPlus.Caching;

public sealed class CacheInvalidationService : IHostedService, IDisposable
{
    // Short enough to finish before a client returning to the home screen asks again,
    // long enough to fold a burst (marking a whole season played) into one rebuild.
    private static readonly TimeSpan UserChangeRebuildDelay = TimeSpan.FromMilliseconds(500);

    // A library scan adds items in bursts; wait for it to settle before rebuilding everyone.
    private static readonly TimeSpan LibraryChangeRebuildDelay = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);

    private readonly MergedListCache _cache;
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<CacheInvalidationService> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _refreshLoop;

    public CacheInvalidationService(
        MergedListCache cache,
        IUserDataManager userDataManager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILogger<CacheInvalidationService> logger)
    {
        _cache = cache;
        _userDataManager = userDataManager;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved += OnUserDataSaved;
        _libraryManager.ItemAdded += OnLibraryItemChanged;
        _libraryManager.ItemRemoved += OnLibraryItemChanged;
        _userManager.OnUserUpdated += OnUserUpdated;
        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged += OnConfigurationChanged;
        }

        _refreshLoop = RefreshLoopAsync(_stopping.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        _libraryManager.ItemAdded -= OnLibraryItemChanged;
        _libraryManager.ItemRemoved -= OnLibraryItemChanged;
        _userManager.OnUserUpdated -= OnUserUpdated;
        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged -= OnConfigurationChanged;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_refreshLoop is not null)
        {
            await _refreshLoop.ConfigureAwait(false);
        }
    }

    public void Dispose() => _stopping.Dispose();

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        if (e.Item is not Video)
        {
            return;
        }

        switch (e.SaveReason)
        {
            case UserDataSaveReason.UpdateUserRating:
                return;
            case UserDataSaveReason.PlaybackProgress:
                _cache.InvalidateForPlaybackProgress(e.UserId, e.Item.Id, UserChangeRebuildDelay);
                return;
            default:
                _cache.Invalidate(e.UserId, UserChangeRebuildDelay);
                return;
        }
    }

    private void OnLibraryItemChanged(object? sender, ItemChangeEventArgs e)
    {
        // Virtual (missing) episodes never appear in either list.
        if (e.Item.IsVirtualItem || e.Item is not (Video or Series or Season))
        {
            return;
        }

        _cache.InvalidateAll(LibraryChangeRebuildDelay);
    }

    private void OnUserUpdated(object? sender, GenericEventArgs<User> e)
        => _cache.Invalidate(e.Argument.Id, UserChangeRebuildDelay);

    private void OnConfigurationChanged(object? sender, MediaBrowser.Model.Plugins.BasePluginConfiguration e)
        => _cache.InvalidateAll(TimeSpan.Zero);

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(RefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    _cache.RefreshExpiring();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[ContinueWatchingPlus] Cache refresh failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
