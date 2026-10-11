using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.ContinueWatchingTweaks.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jellyfin.Plugin.ContinueWatchingTweaks.Api;

public class ResumeInterceptMiddleware
{
    private static readonly Regex _userItemsResumePattern =
        new(@"^/UserItems/Resume$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Older, obsolete route that Infuse also calls on launch.
    private static readonly Regex _legacyUserResumePattern =
        new(@"^/Users/(?<userId>[0-9a-fA-F-]{32,36})/Items/Resume$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex _nextUpPattern =
        new(@"^/Shows/NextUp$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly ItemFields[] _clientFields =
    {
        ItemFields.PrimaryImageAspectRatio,
        ItemFields.ChildCount,
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<ResumeInterceptMiddleware> _logger;

    public ResumeInterceptMiddleware(RequestDelegate next, ILogger<ResumeInterceptMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var pluginConfiguration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var isGetRequest = string.Equals(context.Request.Method, "GET", StringComparison.OrdinalIgnoreCase);

        if (!pluginConfiguration.Enabled || !isGetRequest ||
            pluginConfiguration.ApplyToInfuseOnly && !IsInfuse(context.Request))
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path.Value ?? string.Empty;

        if (pluginConfiguration.BlockNextUpEndpoint && _nextUpPattern.IsMatch(path))
        {
            await WriteEmptyResultAsync(context);
            return;
        }

        var userId = Guid.Empty;
        var legacyMatch = _legacyUserResumePattern.Match(path);

        var isResumeRequest =
            (_userItemsResumePattern.IsMatch(path)
             && Guid.TryParse(context.Request.Query["userId"].ToString(), out userId))
            || (legacyMatch.Success
                && Guid.TryParse(legacyMatch.Groups["userId"].Value, out userId));

        if (!isResumeRequest)
        {
            await _next(context);
            return;
        }

        var mediaTypes = context.Request.Query["mediaTypes"].ToString();
        if (!string.IsNullOrEmpty(mediaTypes) &&
            !mediaTypes.Split(',').Any(t => t.Trim().Equals("Video", StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        await HandleMergedAsync(context, userId, pluginConfiguration);
    }

    private async Task HandleMergedAsync(HttpContext context, Guid userId, PluginConfiguration pluginConfiguration)
    {
        var userManager = context.RequestServices.GetRequiredService<IUserManager>();
        var libraryManager = context.RequestServices.GetRequiredService<ILibraryManager>();
        var tvSeriesManager = context.RequestServices.GetRequiredService<ITVSeriesManager>();
        var dtoService = context.RequestServices.GetRequiredService<IDtoService>();
        var userDataManager = context.RequestServices.GetRequiredService<IUserDataManager>();

        var user = userManager.GetUserById(userId);
        if (user is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var requestQuery = context.Request.Query;

        var startIndex = TryGetInt(requestQuery, "startIndex");
        var limit = TryGetInt(requestQuery, "limit");
        var requestedFields = requestQuery.TryGetValue("fields", out var fieldsValue) ? fieldsValue.ToString() : null;
        var enableImages = TryGetBool(requestQuery, "enableImages");
        var imageTypeLimit = TryGetInt(requestQuery, "imageTypeLimit");
        var enabledImageTypes = requestQuery.TryGetValue("enableImageTypes", out var imageTypesValue)
            ? imageTypesValue.ToString()
            : null;
        var enableUserData = TryGetBool(requestQuery, "enableUserData");
        var enableTotalRecordCount = TryGetBool(requestQuery, "enableTotalRecordCount") ?? true;

        // The merged list can never need more than startIndex + limit items from either source.
        // No limit requested means no cap, which is what Jellyfin's own endpoints do.
        int? maximumItemsPerSource = limit.HasValue ? (startIndex ?? 0) + limit.Value : null;

        var dtoOptions = BuildDtoOptions(requestedFields, enableImages, imageTypeLimit, enabledImageTypes,
            enableUserData);

        // 1. Continue Watching: items the user has partially played (folders excluded)
        var continueWatchingItems = libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
            IsResumable = true,
            IsFolder = false,
            Limit = maximumItemsPerSource,
            Recursive = true,
            DtoOptions = dtoOptions,
        }).ToList();

        // 2. Next Up: optionally skip series that already have an in-progress episode
        var seriesIdsInContinueWatching = continueWatchingItems
            .OfType<Episode>()
            .Select(episode => episode.SeriesId)
            .Where(seriesId => seriesId != Guid.Empty)
            .ToHashSet();

        var nextUpItems = tvSeriesManager.GetNextUp(
                new NextUpQuery { User = user, Limit = maximumItemsPerSource, EnableTotalRecordCount = false },
                dtoOptions)
            .Items
            .Where(item => !pluginConfiguration.HideNextUpForSeriesInProgress
                           || item is not Episode nextUpCandidate
                           || !seriesIdsInContinueWatching.Contains(nextUpCandidate.SeriesId))
            .ToList();

        var nextUpItemIds = nextUpItems.Select(item => item.Id).ToHashSet();

        // 3. Merge. Everything needed for sorting is computed up front so user data is fetched in one batch.
        // For each Next Up series, find the most recently played episode (one single-row query per series).
        var lastWatchedEpisodeBySeriesId = new Dictionary<Guid, BaseItem>();

        var nextUpSeriesIds = nextUpItems
            .OfType<Episode>()
            .Select(episode => episode.SeriesId)
            .Where(seriesId => seriesId != Guid.Empty)
            .Distinct();

        foreach (var seriesId in nextUpSeriesIds)
        {
            var mostRecentlyPlayedEpisode = libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                AncestorIds = new[] { seriesId },
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                IsPlayed = true,
                Recursive = true,
                OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
                Limit = 1,
            }).FirstOrDefault();

            if (mostRecentlyPlayedEpisode is not null)
            {
                lastWatchedEpisodeBySeriesId[seriesId] = mostRecentlyPlayedEpisode;
            }
        }

        var userDataByItemId = userDataManager.GetUserDataBatch(
            continueWatchingItems.Concat(lastWatchedEpisodeBySeriesId.Values).ToList(),
            user);

        DateTime GetLastPlayedDate(BaseItem item)
            => userDataByItemId.GetValueOrDefault(item.Id)?.LastPlayedDate ?? DateTime.MinValue;

        // In-progress items sort by their own last-played time.
        // Next Up items sort by the later of: when the series was last watched, or when the episode was added.
        DateTime GetSortDate(BaseItem item)
        {
            if (!nextUpItemIds.Contains(item.Id))
            {
                return GetLastPlayedDate(item);
            }

            var seriesLastPlayedDate =
                item is Episode nextUpEpisode
                && lastWatchedEpisodeBySeriesId.TryGetValue(nextUpEpisode.SeriesId, out var seriesLastWatchedEpisode)
                    ? GetLastPlayedDate(seriesLastWatchedEpisode)
                    : DateTime.MinValue;

            return pluginConfiguration.SortNextUpByEpisodeAddedDate && item.DateCreated > seriesLastPlayedDate
                ? item.DateCreated
                : seriesLastPlayedDate;
        }

        var mergedItems = continueWatchingItems
            .Concat(nextUpItems)
            .OrderByDescending(GetSortDate)
            .ToList();

        _logger.LogDebug(
            "[ContinueWatchingTweaks] {Path}: {ContinueWatchingCount} continue-watching + {NextUpCount} next-up = {TotalCount} total",
            context.Request.Path, continueWatchingItems.Count, nextUpItems.Count, mergedItems.Count);

        // 4. Page
        var firstIndex = startIndex ?? 0;
        IEnumerable<BaseItem> pagedItems = mergedItems.Skip(firstIndex);
        if (limit.HasValue)
        {
            pagedItems = pagedItems.Take(limit.Value);
        }

        // 5. Convert to DTOs and return
        var itemDtos = dtoService.GetBaseItemDtos(pagedItems.ToList(), dtoOptions, user);

        var queryResult = new QueryResult<BaseItemDto>
        {
            Items = itemDtos,
            TotalRecordCount = enableTotalRecordCount ? mergedItems.Count : 0,
            StartIndex = firstIndex,
        };

        var serializerOptions = context.RequestServices
                                    .GetService<IOptions<JsonOptions>>()?.Value.JsonSerializerOptions
                                ?? JsonDefaults.CamelCaseOptions;

        context.Response.ContentType = "application/json; charset=utf-8";
        var json = JsonSerializer.Serialize(queryResult, serializerOptions);
        await context.Response.WriteAsync(json).ConfigureAwait(false);
    }

    private static async Task WriteEmptyResultAsync(HttpContext context)
    {
        var serializerOptions = context.RequestServices
                                    .GetService<IOptions<JsonOptions>>()?.Value.JsonSerializerOptions
                                ?? JsonDefaults.CamelCaseOptions;

        var emptyResult = new QueryResult<BaseItemDto>
        {
            Items = Array.Empty<BaseItemDto>(),
            TotalRecordCount = 0,
            StartIndex = 0,
        };

        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(JsonSerializer.Serialize(emptyResult, serializerOptions))
            .ConfigureAwait(false);
    }

    private static int? TryGetInt(IQueryCollection qs, string key)
        => qs.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : null;

    private static bool? TryGetBool(IQueryCollection qs, string key)
        => qs.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : null;

    private static DtoOptions BuildDtoOptions(
        string? fields, bool? enableImages, int? imageTypeLimit,
        string? enableImageTypes, bool? enableUserData)
    {
        var options = new DtoOptions
        {
            EnableImages = enableImages ?? true,
            ImageTypeLimit = imageTypeLimit ?? 1,
            EnableUserData = enableUserData ?? true,
        };

        var fieldList = new List<ItemFields>(_clientFields);

        if (!string.IsNullOrEmpty(fields))
        {
            fieldList.AddRange(fields
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => Enum.TryParse<ItemFields>(f.Trim(), true, out var p) ? p : (ItemFields?)null)
                .Where(f => f.HasValue).Select(f => f!.Value));
        }

        options.Fields = fieldList.Distinct().ToArray();

        if (!string.IsNullOrEmpty(enableImageTypes))
        {
            options.ImageTypes = enableImageTypes
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => Enum.TryParse<ImageType>(t.Trim(), true, out var p) ? p : (ImageType?)null)
                .Where(t => t.HasValue).Select(t => t!.Value)
                .ToArray();
        }

        return options;
    }

    private static bool IsInfuse(HttpRequest request)
    {
        foreach (var header in new[] { "User-Agent", "Authorization", "X-Emby-Authorization" })
        {
            if (request.Headers.TryGetValue(header, out var value)
                && value.ToString().Contains("Infuse", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}