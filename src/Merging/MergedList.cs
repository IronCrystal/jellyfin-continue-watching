using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.ContinueWatchingPlus.Merging;

public sealed record MergedEntry(Guid ItemId, BaseItemKind Kind, bool IsNextUp, DateTime SortDate);

public sealed class MergedList
{
    private readonly HashSet<Guid> _inProgressIds;

    public MergedList(IReadOnlyList<MergedEntry> entries, DateTime builtAtUtc)
    {
        Entries = entries;
        BuiltAtUtc = builtAtUtc;
        _inProgressIds = entries.Where(entry => !entry.IsNextUp).Select(entry => entry.ItemId).ToHashSet();
    }

    public IReadOnlyList<MergedEntry> Entries { get; }

    public DateTime BuiltAtUtc { get; }

    public bool ContainsInProgress(Guid itemId) => _inProgressIds.Contains(itemId);
}
