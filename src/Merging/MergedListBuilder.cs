using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.ContinueWatchingPlus.Configuration;
using Jellyfin.Plugin.ContinueWatchingPlus.Preferences;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContinueWatchingPlus.Merging;

public sealed class MergedListBuilder
{
    internal const int MaxItems = 100;

    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ITVSeriesManager _tvSeriesManager;
    private readonly IUserDataManager _userDataManager;
    private readonly NextUpPreferenceStore _nextUpPreferences;
    private readonly ILogger<MergedListBuilder> _logger;

    public MergedListBuilder(
        IUserManager userManager,
        ILibraryManager libraryManager,
        ITVSeriesManager tvSeriesManager,
        IUserDataManager userDataManager,
        NextUpPreferenceStore nextUpPreferences,
        ILogger<MergedListBuilder> logger)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _tvSeriesManager = tvSeriesManager;
        _userDataManager = userDataManager;
        _nextUpPreferences = nextUpPreferences;
        _logger = logger;
    }

    public MergedList Build(Guid userId)
    {
        var builtAtUtc = DateTime.UtcNow;
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return new MergedList(Array.Empty<MergedEntry>(), builtAtUtc);
        }

        var nextUpPreferences = _nextUpPreferences.Get(userId, configuration);
        var dtoOptions = DtoOptions.StoredColumnsOnly;

        var continueWatchingItems = GetContinueWatchingItems(user, dtoOptions);

        // Next Up computes every series before applying a limit, so asking for all of it costs the same
        // and lets a long-idle series with a newly added episode still reach the front.
        var nextUpItems = _tvSeriesManager.GetNextUp(
                new NextUpQuery
                {
                    User = user,
                    EnableTotalRecordCount = false,
                    NextUpDateCutoff = builtAtUtc.Date.AddDays(-nextUpPreferences.MaxDays),
                    EnableResumable = false,
                    EnableRewatching = nextUpPreferences.EnableRewatching,
                },
                dtoOptions)
            .Items;

        var seriesIdsInContinueWatching = continueWatchingItems
            .OfType<Episode>()
            .Select(episode => episode.SeriesId)
            .Where(seriesId => !seriesId.Equals(Guid.Empty))
            .ToHashSet();

        var nextUpEpisodes = nextUpItems
            .OfType<Episode>()
            .Where(episode => !configuration.HideNextUpForSeriesInProgress
                              || !seriesIdsInContinueWatching.Contains(episode.SeriesId))
            .ToList();

        var lastWatchedByNextUpEpisodeId = GetLastWatchedEpisodes(user, nextUpEpisodes, nextUpPreferences.EnableRewatching, dtoOptions);

        // Jellyfin adds a series to Next Up as soon as any of its episodes has a last-played date,
        // even if it was only opened briefly. Optionally keep only series with a finished episode.
        if (configuration.RequireFinishedEpisodeForNextUp)
        {
            nextUpEpisodes = nextUpEpisodes.Where(episode => lastWatchedByNextUpEpisodeId.ContainsKey(episode.Id)).ToList();
        }

        var userDataByItemId = _userDataManager.GetUserDataBatch(
            continueWatchingItems
                .Concat(lastWatchedByNextUpEpisodeId.Values.SelectMany(GetVersions))
                .DistinctBy(item => item.Id)
                .ToList(),
            user);

        DateTime GetLastPlayedDate(BaseItem item)
            => userDataByItemId.GetValueOrDefault(item.Id)?.LastPlayedDate ?? DateTime.MinValue;

        // Next Up items sort by the later of: when the series' last finished episode was played,
        // or (optionally) when the episode was added.
        DateTime GetNextUpSortDate(Episode episode)
        {
            var seriesLastPlayedDate = lastWatchedByNextUpEpisodeId.TryGetValue(episode.Id, out var lastWatched)
                ? GetVersions(lastWatched).Select(GetLastPlayedDate).DefaultIfEmpty(DateTime.MinValue).Max()
                : DateTime.MinValue;

            return configuration.SortNextUpByEpisodeAddedDate && episode.DateCreated > seriesLastPlayedDate
                ? episode.DateCreated
                : seriesLastPlayedDate;
        }

        var entries = continueWatchingItems
            .Select(item => new MergedEntry(item.Id, item.GetBaseItemKind(), false, GetLastPlayedDate(item)))
            .Concat(nextUpEpisodes.Select(episode => new MergedEntry(episode.Id, episode.GetBaseItemKind(), true, GetNextUpSortDate(episode))))
            .DistinctBy(entry => entry.ItemId)
            .OrderByDescending(entry => entry.SortDate)
            .Take(MaxItems)
            .ToList();

        _logger.LogDebug(
            "[ContinueWatchingPlus] Built list for {UserId}: {ContinueWatchingCount} in progress + {NextUpCount} next up in {Milliseconds} ms",
            userId,
            continueWatchingItems.Count,
            nextUpEpisodes.Count,
            (DateTime.UtcNow - builtAtUtc).TotalMilliseconds);

        return new MergedList(entries, builtAtUtc);
    }

    // Mirrors the query behind Jellyfin's own /UserItems/Resume, limited to video.
    private List<BaseItem> GetContinueWatchingItems(User user, DtoOptions dtoOptions)
    {
        var excludedFolderIds = user.GetPreferenceValues<Guid>(PreferenceKind.LatestItemExcludes);
        var ancestorIds = excludedFolderIds.Length == 0
            ? Array.Empty<Guid>()
            : _libraryManager.GetUserRootFolder().GetChildren(user, true)
                .Where(item => item is Folder && !excludedFolderIds.Contains(item.Id))
                .Select(item => item.Id)
                .ToArray();

        return _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
            IsResumable = true,
            Limit = MaxItems,
            Recursive = true,
            DtoOptions = dtoOptions,
            MediaTypes = new[] { MediaType.Video },
            IsVirtualItem = false,
            CollapseBoxSetItems = false,
            IncludeOwnedItems = true,
            AncestorIds = ancestorIds,
        }).ToList();
    }

    // The episode each Next Up entry continues from. A rewatch entry continues from the most recently
    // played episode; a regular entry from the furthest finished one.
    private Dictionary<Guid, BaseItem> GetLastWatchedEpisodes(User user, List<Episode> nextUpEpisodes, bool enableRewatching, DtoOptions dtoOptions)
    {
        var seriesKeys = nextUpEpisodes
            .Select(episode => episode.SeriesPresentationUniqueKey)
            .Where(seriesKey => !string.IsNullOrEmpty(seriesKey))
            .Distinct()
            .ToList();

        var batchResultBySeriesKey = _libraryManager.GetNextUpEpisodesBatch(
            new InternalItemsQuery(user) { DtoOptions = dtoOptions },
            seriesKeys,
            includeSpecials: false,
            includeWatchedForRewatching: enableRewatching);

        var playedEpisodeIds = enableRewatching
            ? _userDataManager.GetUserDataBatch(nextUpEpisodes.Cast<BaseItem>().ToList(), user)
                .Where(pair => pair.Value.Played)
                .Select(pair => pair.Key)
                .ToHashSet()
            : new HashSet<Guid>();

        var lastWatchedByNextUpEpisodeId = new Dictionary<Guid, BaseItem>();
        foreach (var episode in nextUpEpisodes)
        {
            if (!batchResultBySeriesKey.TryGetValue(episode.SeriesPresentationUniqueKey, out var batchResult))
            {
                continue;
            }

            var lastWatched = playedEpisodeIds.Contains(episode.Id)
                ? batchResult.LastWatchedForRewatching
                : batchResult.LastWatched;

            if (lastWatched is not null)
            {
                lastWatchedByNextUpEpisodeId[episode.Id] = lastWatched;
            }
        }

        return lastWatchedByNextUpEpisodeId;
    }

    private static IEnumerable<BaseItem> GetVersions(BaseItem item)
        => item is Video video ? video.GetAllVersions() : new[] { item };
}
