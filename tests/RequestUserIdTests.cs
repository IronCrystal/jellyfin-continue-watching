using System;
using System.Security.Claims;
using Jellyfin.Plugin.ContinueWatchingPlus.Api;
using MediaBrowser.Controller.Net;
using Xunit;

namespace Jellyfin.Plugin.ContinueWatchingPlus.Tests;

public class RequestUserIdTests
{
    private static readonly Guid UserA = Guid.NewGuid();
    private static readonly Guid UserB = Guid.NewGuid();

    private static ClaimsPrincipal Principal(Guid userId, bool isAdministrator = false)
    {
        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim("Jellyfin-UserId", userId.ToString("N")),
                new Claim(ClaimTypes.Role, isAdministrator ? "Administrator" : "User"),
            },
            "CustomAuthentication");
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public void NoUserId_UsesTheCallersOwn()
        => Assert.Equal(UserA, ContinueWatchingActionFilter.ResolveRequestUserId(Principal(UserA), null));

    [Fact]
    public void EmptyUserId_UsesTheCallersOwn()
        => Assert.Equal(UserA, ContinueWatchingActionFilter.ResolveRequestUserId(Principal(UserA), Guid.Empty));

    [Fact]
    public void OwnUserId_IsAllowed()
        => Assert.Equal(UserA, ContinueWatchingActionFilter.ResolveRequestUserId(Principal(UserA), UserA));

    [Fact]
    public void AnotherUsersId_IsForbidden()
        => Assert.Throws<SecurityException>(() => ContinueWatchingActionFilter.ResolveRequestUserId(Principal(UserA), UserB));

    [Fact]
    public void AnotherUsersId_IsAllowedForAdministrators()
        => Assert.Equal(UserB, ContinueWatchingActionFilter.ResolveRequestUserId(Principal(UserA, isAdministrator: true), UserB));

    [Fact]
    public void ApiKeyWithoutUser_CanOnlyActAsAdministrator()
    {
        var apiKey = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, "Administrator") }, "CustomAuthentication"));
        Assert.Equal(UserB, ContinueWatchingActionFilter.ResolveRequestUserId(apiKey, UserB));
        Assert.Equal(Guid.Empty, ContinueWatchingActionFilter.ResolveRequestUserId(apiKey, null));
    }
}
