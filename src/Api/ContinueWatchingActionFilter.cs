using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ContinueWatchingPlus.Caching;
using Jellyfin.Plugin.ContinueWatchingPlus.Configuration;
using Jellyfin.Plugin.ContinueWatchingPlus.Merging;
using Jellyfin.Plugin.ContinueWatchingPlus.Preferences;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContinueWatchingPlus.Api;

/// <summary>
/// Runs inside Jellyfin's MVC pipeline on its own resume and Next Up actions, after Jellyfin's
/// [Authorize] has already rejected unauthenticated requests.
/// </summary>
public sealed class ContinueWatchingActionFilter : IAsyncActionFilter
{
    // Values from Jellyfin.Api's InternalClaimTypes and UserRoles, which plugins can't reference.
    private const string UserIdClaimType = "Jellyfin-UserId";
    private const string ClientClaimType = "Jellyfin-Client";
    private const string AdministratorRole = "Administrator";

    // Apps that run jellyfin-web, whose Next Up request carries the user's own web settings. Jellyfin Media
    // Player is the desktop app up to 1.x; it reports "Jellyfin Desktop" from 2.0. Native apps (Android TV,
    // Swiftfin, ...) may send cutoffs of their own and must not overwrite them.
    private static readonly string[] _webClientNames =
    {
        "Jellyfin Web",
        "Jellyfin Media Player",
        "Jellyfin Desktop",
        "Jellyfin for Android",
    };

    private readonly MergedListCache _cache;
    private readonly NextUpPreferenceStore _nextUpPreferences;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IDtoService _dtoService;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<ContinueWatchingActionFilter> _logger;
    private readonly Func<PluginConfiguration?> _getConfiguration;

    public ContinueWatchingActionFilter(
        MergedListCache cache,
        NextUpPreferenceStore nextUpPreferences,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IDtoService dtoService,
        ISessionManager sessionManager,
        ILogger<ContinueWatchingActionFilter> logger)
        : this(cache, nextUpPreferences, userManager, libraryManager, dtoService, sessionManager, logger, () => Plugin.Instance?.Configuration)
    {
    }

    internal ContinueWatchingActionFilter(
        MergedListCache cache,
        NextUpPreferenceStore nextUpPreferences,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IDtoService dtoService,
        ISessionManager sessionManager,
        ILogger<ContinueWatchingActionFilter> logger,
        Func<PluginConfiguration?> getConfiguration)
    {
        _cache = cache;
        _nextUpPreferences = nextUpPreferences;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _dtoService = dtoService;
        _sessionManager = sessionManager;
        _logger = logger;
        _getConfiguration = getConfiguration;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var configuration = _getConfiguration();
        if (configuration is null
            || !configuration.Enabled
            || context.ActionDescriptor is not ControllerActionDescriptor action
            || context.HttpContext.User.Identity?.IsAuthenticated != true)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var handled = (action.ControllerName, action.ActionName) switch
        {
            ("Items", "GetResumeItems" or "GetResumeItemsLegacy") => await TryHandleResumeAsync(context, configuration).ConfigureAwait(false),
            ("TvShows", "GetNextUp") => TryHandleNextUp(context, configuration),
            _ => false,
        };

        if (!handled)
        {
            await next().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Same rule as Jellyfin's RequestHelpers.GetUserId: no user id means the caller's own;
    /// another user's id requires an administrator. Jellyfin's ExceptionMiddleware turns its own
    /// MediaBrowser.Controller.Net.SecurityException (not System.Security's) into the standard 403.
    /// </summary>
    internal static Guid ResolveRequestUserId(ClaimsPrincipal principal, Guid? requestedUserId)
    {
        var authenticatedUserId = GetAuthenticatedUserId(principal);
        if (requestedUserId is null || requestedUserId.Value.Equals(Guid.Empty))
        {
            return authenticatedUserId;
        }

        if (!requestedUserId.Value.Equals(authenticatedUserId) && !principal.IsInRole(AdministratorRole))
        {
            throw new SecurityException("Forbidden");
        }

        return requestedUserId.Value;
    }

    private static bool IsWebClient(ClaimsPrincipal principal)
    {
        var client = principal.Claims
            .FirstOrDefault(claim => claim.Type.Equals(ClientClaimType, StringComparison.OrdinalIgnoreCase))
            ?.Value;
        return client is not null && _webClientNames.Contains(client, StringComparer.OrdinalIgnoreCase);
    }

    private static Guid GetAuthenticatedUserId(ClaimsPrincipal principal)
    {
        var value = principal.Claims
            .FirstOrDefault(claim => claim.Type.Equals(UserIdClaimType, StringComparison.OrdinalIgnoreCase))
            ?.Value;
        return string.IsNullOrEmpty(value) ? Guid.Empty : Guid.Parse(value);
    }

    private async Task<bool> TryHandleResumeAsync(ActionExecutingContext context, PluginConfiguration configuration)
    {
        var arguments = context.ActionArguments;

        if (configuration.ApplyToInfuseOnly && !IsInfuse(context.HttpContext.Request))
        {
            return false;
        }

        // Continue Listening and Continue Reading keep Jellyfin's own lists.
        var mediaTypes = GetArgument<MediaType[]>(arguments, "mediaTypes") ?? Array.Empty<MediaType>();
        if (mediaTypes.Length > 0 && !mediaTypes.Contains(MediaType.Video))
        {
            return false;
        }

        // A library-scoped or search query isn't the home row.
        if (GetValue<Guid>(arguments, "parentId") is { } parentId && !parentId.Equals(Guid.Empty)
            || !string.IsNullOrEmpty(GetArgument<string>(arguments, "searchTerm")))
        {
            return false;
        }

        var userId = ResolveRequestUserId(context.HttpContext.User, GetValue<Guid>(arguments, "userId"));
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            context.Result = new NotFoundResult();
            return true;
        }

        QueryResult<BaseItemDto> result;
        try
        {
            result = await BuildResumeResultAsync(arguments, user).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A bug or database error here degrades to Jellyfin's own Continue Watching list, not a broken row.
            _logger.LogError(ex, "[ContinueWatchingPlus] Could not build the merged list for user {UserId}; serving Jellyfin's own list", user.Id);
            return false;
        }

        context.Result = new OkObjectResult(result);
        return true;
    }

    private async Task<QueryResult<BaseItemDto>> BuildResumeResultAsync(IDictionary<string, object?> arguments, User user)
    {
        var mergedList = await _cache.GetAsync(user.Id).ConfigureAwait(false);

        var includeItemTypes = GetArgument<BaseItemKind[]>(arguments, "includeItemTypes") ?? Array.Empty<BaseItemKind>();
        var excludeItemTypes = GetArgument<BaseItemKind[]>(arguments, "excludeItemTypes") ?? Array.Empty<BaseItemKind>();
        var excludedItemIds = GetValue<bool>(arguments, "excludeActiveSessions") == true
            ? GetActiveSessionItemIds(user.Id)
            : new HashSet<Guid>();

        var matchingEntries = mergedList.Entries
            .Where(entry => (includeItemTypes.Length == 0 || includeItemTypes.Contains(entry.Kind))
                            && !excludeItemTypes.Contains(entry.Kind)
                            && !excludedItemIds.Contains(entry.ItemId))
            .ToList();

        var startIndex = GetValue<int>(arguments, "startIndex");
        var limit = GetValue<int>(arguments, "limit");
        IEnumerable<MergedEntry> pageEntries = matchingEntries.Skip(Math.Max(0, startIndex ?? 0));
        if (limit.HasValue)
        {
            pageEntries = pageEntries.Take(Math.Max(0, limit.Value));
        }

        var pageItems = new List<BaseItem>();
        var sortDateByNextUpItemId = new Dictionary<Guid, DateTime>();
        foreach (var entry in pageEntries)
        {
            // Re-checks the user's access on every request, not only when the list was built.
            if (_libraryManager.GetItemById<BaseItem>(entry.ItemId, user) is not { } item)
            {
                continue;
            }

            pageItems.Add(item);
            if (entry.IsNextUp)
            {
                sortDateByNextUpItemId[entry.ItemId] = entry.SortDate;
            }
        }

        var dtoOptions = BuildDtoOptions(arguments);
        var itemDtos = _dtoService.GetBaseItemDtos(pageItems, dtoOptions, user);

        // A Next Up episode can carry its own recent last-played date from a brief play. Replace it with
        // the sort date so clients that order by this field (Infuse) agree with the response order.
        foreach (var itemDto in itemDtos)
        {
            if (itemDto.UserData is not null && sortDateByNextUpItemId.TryGetValue(itemDto.Id, out var sortDate))
            {
                itemDto.UserData.LastPlayedDate = sortDate > DateTime.MinValue ? sortDate : null;
            }
        }

        var enableTotalRecordCount = GetValue<bool>(arguments, "enableTotalRecordCount") ?? true;
        return new QueryResult<BaseItemDto>(
            startIndex,
            enableTotalRecordCount ? matchingEntries.Count : 0,
            itemDtos);
    }

    private bool TryHandleNextUp(ActionExecutingContext context, PluginConfiguration configuration)
    {
        var arguments = context.ActionArguments;
        var principal = context.HttpContext.User;
        var userId = ResolveRequestUserId(principal, GetValue<Guid>(arguments, "userId"));

        // jellyfin-web's home Next Up request carries the user's own "Max days in Next Up" and
        // "Enable rewatching" settings, which are otherwise stored only in the browser.
        if (GetValue<DateTime>(arguments, "nextUpDateCutoff") is { } nextUpDateCutoff
            && !userId.Equals(Guid.Empty)
            && userId.Equals(GetAuthenticatedUserId(principal))
            && IsWebClient(principal)
            && _nextUpPreferences.Record(userId, nextUpDateCutoff, GetValue<bool>(arguments, "enableRewatching") ?? false, DateTime.UtcNow))
        {
            _cache.Invalidate(userId, TimeSpan.Zero);
        }

        // A series-specific request finds the episode to play next on a series page; leave it alone.
        if (!configuration.BlockNextUpEndpoint
            || (configuration.ApplyToInfuseOnly && !IsInfuse(context.HttpContext.Request))
            || GetValue<Guid>(arguments, "seriesId") is { } seriesId && !seriesId.Equals(Guid.Empty))
        {
            return false;
        }

        if (_userManager.GetUserById(userId) is null)
        {
            context.Result = new NotFoundResult();
            return true;
        }

        context.Result = new OkObjectResult(new QueryResult<BaseItemDto>(
            GetValue<int>(arguments, "startIndex"),
            0,
            Array.Empty<BaseItemDto>()));
        return true;
    }

    private HashSet<Guid> GetActiveSessionItemIds(Guid userId)
        => _sessionManager.Sessions
            .Where(session => session.UserId.Equals(userId) && session.NowPlayingItem is not null)
            .SelectMany(session => _libraryManager.GetItemById(session.NowPlayingItem.Id) is Video video
                ? video.GetAllVersions().Select(version => version.Id)
                : new[] { session.NowPlayingItem.Id })
            .ToHashSet();

    // Same as Jellyfin's DtoExtensions.AddAdditionalDtoOptions.
    private static DtoOptions BuildDtoOptions(IDictionary<string, object?> arguments)
    {
        var dtoOptions = new DtoOptions
        {
            Fields = GetArgument<ItemFields[]>(arguments, "fields") ?? Array.Empty<ItemFields>(),
            EnableImages = GetValue<bool>(arguments, "enableImages") ?? true,
        };

        if (GetValue<int>(arguments, "imageTypeLimit") is { } imageTypeLimit)
        {
            dtoOptions.ImageTypeLimit = imageTypeLimit;
        }

        if (GetValue<bool>(arguments, "enableUserData") is { } enableUserData)
        {
            dtoOptions.EnableUserData = enableUserData;
        }

        if (GetArgument<ImageType[]>(arguments, "enableImageTypes") is { Length: > 0 } enableImageTypes)
        {
            dtoOptions.ImageTypes = enableImageTypes;
        }

        return dtoOptions;
    }

    private static T? GetArgument<T>(IDictionary<string, object?> arguments, string name)
        where T : class
        => arguments.TryGetValue(name, out var value) ? value as T : null;

    private static TValue? GetValue<TValue>(IDictionary<string, object?> arguments, string name)
        where TValue : struct
        => arguments.TryGetValue(name, out var value) && value is TValue typed ? typed : null;

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
