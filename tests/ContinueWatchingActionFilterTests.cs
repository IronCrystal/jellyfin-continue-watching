using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ContinueWatchingPlus.Api;
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
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.ContinueWatchingPlus.Tests;

public sealed class ContinueWatchingActionFilterTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;
    private readonly User _alice = new("alice", "auth", "reset");
    private readonly User _bob = new("bob", "auth", "reset");
    private readonly IUserManager _userManager = Substitute.For<IUserManager>();
    private readonly IDtoService _dtoService = Substitute.For<IDtoService>();
    private readonly NextUpPreferenceStore _preferences;
    private Func<Guid, MergedList> _build;
    private MergedListCache? _cache;
    private bool _nextCalled;

    public ContinueWatchingActionFilterTests()
    {
        _userManager.GetUserById(_alice.Id).Returns(_alice);
        _userManager.GetUserById(_bob.Id).Returns(_bob);
        _dtoService.GetBaseItemDtos(Arg.Any<IReadOnlyList<BaseItem>>(), Arg.Any<DtoOptions>(), Arg.Any<User?>(), Arg.Any<BaseItem?>())
            .Returns(Array.Empty<BaseItemDto>());
        _preferences = new NextUpPreferenceStore(Path.Combine(_directory, "prefs.json"), NullLogger<NextUpPreferenceStore>.Instance);
        _build = _ => new MergedList(Array.Empty<MergedEntry>(), DateTime.UtcNow);
    }

    public void Dispose()
    {
        _cache?.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private ContinueWatchingActionFilter CreateFilter()
    {
        _cache = new MergedListCache(userId => _build(userId), () => TimeSpan.FromMinutes(5), TimeProvider.System, NullLogger.Instance);
        return new ContinueWatchingActionFilter(
            _cache,
            _preferences,
            _userManager,
            Substitute.For<ILibraryManager>(),
            _dtoService,
            Substitute.For<ISessionManager>(),
            NullLogger<ContinueWatchingActionFilter>.Instance,
            () => new PluginConfiguration());
    }

    private static ClaimsPrincipal Principal(User user, string client = "Jellyfin Web")
        => new(new ClaimsIdentity(
            new[]
            {
                new Claim("Jellyfin-UserId", user.Id.ToString("N")),
                new Claim("Jellyfin-Client", client),
                new Claim(ClaimTypes.Role, "User"),
            },
            "CustomAuthentication"));

    private async Task<ActionExecutingContext> RunAsync(string controller, string action, ClaimsPrincipal principal, Dictionary<string, object?> arguments)
    {
        var actionContext = new ActionContext(
            new DefaultHttpContext { User = principal },
            new RouteData(),
            new ControllerActionDescriptor { ControllerName = controller, ActionName = action });
        var context = new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), arguments, controller: new object());

        await CreateFilter().OnActionExecutionAsync(context, () =>
        {
            _nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), new object()));
        });
        return context;
    }

    [Fact]
    public async Task OwnList_IsAnsweredByThePlugin()
    {
        var context = await RunAsync("Items", "GetResumeItems", Principal(_alice), new() { ["userId"] = _alice.Id });

        Assert.False(_nextCalled);
        Assert.IsType<QueryResult<BaseItemDto>>(Assert.IsType<OkObjectResult>(context.Result).Value);
    }

    [Fact]
    public async Task WhenBuildingTheListFails_JellyfinsOwnListIsServed()
    {
        _build = _ => throw new InvalidOperationException("database unavailable");

        var context = await RunAsync("Items", "GetResumeItems", Principal(_alice), new() { ["userId"] = _alice.Id });

        Assert.True(_nextCalled);
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task AnotherUsersList_IsForbiddenEvenWhenBuildingWouldFail()
    {
        _build = _ => throw new InvalidOperationException("database unavailable");

        await Assert.ThrowsAsync<SecurityException>(
            () => RunAsync("Items", "GetResumeItems", Principal(_bob), new() { ["userId"] = _alice.Id }));
        Assert.False(_nextCalled);
    }

    [Fact]
    public async Task NextUpSettings_AreLearnedFromTheWebApp()
    {
        await RunAsync("TvShows", "GetNextUp", Principal(_alice, "Jellyfin Web"), new()
        {
            ["nextUpDateCutoff"] = DateTime.UtcNow.Date.AddDays(-30),
            ["enableRewatching"] = true,
        });

        Assert.Equal(new NextUpPreferences(30, true), _preferences.Get(_alice.Id, new PluginConfiguration()));
        Assert.True(_nextCalled);
    }

    [Fact]
    public async Task NextUpSettings_AreNotTakenFromNativeApps()
    {
        await RunAsync("TvShows", "GetNextUp", Principal(_alice, "Android TV"), new()
        {
            ["nextUpDateCutoff"] = DateTime.UtcNow.Date.AddDays(-30),
            ["enableRewatching"] = true,
        });

        Assert.Equal(new NextUpPreferences(365, false), _preferences.Get(_alice.Id, new PluginConfiguration()));
    }
}
