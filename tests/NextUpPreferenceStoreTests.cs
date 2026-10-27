using System;
using System.IO;
using Jellyfin.Plugin.ContinueWatchingPlus.Configuration;
using Jellyfin.Plugin.ContinueWatchingPlus.Preferences;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ContinueWatchingPlus.Tests;

public sealed class NextUpPreferenceStoreTests : IDisposable
{
    private static readonly DateTime UtcNow = new(2026, 10, 27, 15, 0, 0, DateTimeKind.Utc);

    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;
    private readonly Guid _userId = Guid.NewGuid();

    private string FilePath => Path.Combine(_directory, "prefs.json");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private NextUpPreferenceStore CreateStore() => new(FilePath, NullLogger<NextUpPreferenceStore>.Instance);

    [Fact]
    public void UnknownUser_GetsTheConfiguredDefaults()
    {
        var configuration = new PluginConfiguration { DefaultMaxDaysInNextUp = 30, DefaultEnableRewatching = true };

        Assert.Equal(new NextUpPreferences(30, true), CreateStore().Get(_userId, configuration));
    }

    [Fact]
    public void TheWebClientsCutoffDate_IsStoredAsMaxDays()
    {
        var store = CreateStore();

        // jellyfin-web sends today minus "Max days in Next Up", as a date.
        Assert.True(store.Record(_userId, new DateTime(2026, 9, 27), enableRewatching: true, UtcNow));

        Assert.Equal(new NextUpPreferences(30, true), store.Get(_userId, new PluginConfiguration()));
    }

    [Fact]
    public void RecordingTheSameSettings_ReportsNoChange()
    {
        var store = CreateStore();
        store.Record(_userId, new DateTime(2026, 9, 27), false, UtcNow);

        Assert.False(store.Record(_userId, new DateTime(2026, 9, 27), false, UtcNow));
        Assert.True(store.Record(_userId, new DateTime(2026, 9, 27), true, UtcNow));
    }

    [Fact]
    public void Settings_SurviveARestart()
    {
        CreateStore().Record(_userId, new DateTime(2025, 10, 27), false, UtcNow);

        Assert.Equal(new NextUpPreferences(365, false), CreateStore().Get(_userId, new PluginConfiguration()));
    }

    [Fact]
    public void ACorruptFile_FallsBackToDefaults()
    {
        File.WriteAllText(FilePath, "{not json");

        Assert.Equal(new NextUpPreferences(365, false), CreateStore().Get(_userId, new PluginConfiguration()));
    }
}
