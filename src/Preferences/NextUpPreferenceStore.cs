using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.ContinueWatchingPlus.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContinueWatchingPlus.Preferences;

public readonly record struct NextUpPreferences(int MaxDays, bool EnableRewatching);

/// <summary>
/// jellyfin-web keeps "Max days in Next Up" and "Enable rewatching" in browser storage only, so the server
/// never stores them. The web client sends both on its own Next Up request; this store remembers the last
/// values each user's client sent.
/// </summary>
public sealed class NextUpPreferenceStore
{
    private const int MaxDaysLimit = 36500;

    private readonly string _filePath;
    private readonly ILogger<NextUpPreferenceStore> _logger;
    private readonly object _gate = new();
    private Dictionary<Guid, NextUpPreferences>? _byUserId;

    public NextUpPreferenceStore(IApplicationPaths applicationPaths, ILogger<NextUpPreferenceStore> logger)
        : this(Path.Combine(applicationPaths.PluginConfigurationsPath, "ContinueWatchingPlus.NextUpPreferences.json"), logger)
    {
    }

    internal NextUpPreferenceStore(string filePath, ILogger<NextUpPreferenceStore> logger)
    {
        _filePath = filePath;
        _logger = logger;
    }

    public NextUpPreferences Get(Guid userId, PluginConfiguration configuration)
    {
        lock (_gate)
        {
            return Load().TryGetValue(userId, out var preferences)
                ? preferences
                : new NextUpPreferences(Math.Clamp(configuration.DefaultMaxDaysInNextUp, 0, MaxDaysLimit), configuration.DefaultEnableRewatching);
        }
    }

    /// <returns>True if the stored preferences changed.</returns>
    public bool Record(Guid userId, DateTime nextUpDateCutoff, bool enableRewatching, DateTime utcNow)
    {
        var maxDays = Math.Clamp((int)Math.Round((utcNow.Date - nextUpDateCutoff.Date).TotalDays), 0, MaxDaysLimit);
        var preferences = new NextUpPreferences(maxDays, enableRewatching);

        lock (_gate)
        {
            var byUserId = Load();
            if (byUserId.TryGetValue(userId, out var existing) && existing == preferences)
            {
                return false;
            }

            byUserId[userId] = preferences;
            Save(byUserId);
            return true;
        }
    }

    private Dictionary<Guid, NextUpPreferences> Load()
    {
        if (_byUserId is not null)
        {
            return _byUserId;
        }

        try
        {
            _byUserId = File.Exists(_filePath)
                ? JsonSerializer.Deserialize<Dictionary<Guid, NextUpPreferences>>(File.ReadAllText(_filePath)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "[ContinueWatchingPlus] Could not read {Path}; using defaults", _filePath);
            _byUserId = new();
        }

        return _byUserId;
    }

    private void Save(Dictionary<Guid, NextUpPreferences> byUserId)
    {
        try
        {
            var temporaryPath = _filePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(byUserId));
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "[ContinueWatchingPlus] Could not write {Path}", _filePath);
        }
    }
}
