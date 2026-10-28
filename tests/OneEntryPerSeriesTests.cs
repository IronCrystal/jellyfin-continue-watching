using System;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ContinueWatchingPlus.Merging;
using Xunit;
using Candidate = Jellyfin.Plugin.ContinueWatchingPlus.Merging.MergedListBuilder.Candidate;

namespace Jellyfin.Plugin.ContinueWatchingPlus.Tests;

public class OneEntryPerSeriesTests
{
    private static readonly Guid Akudama = Guid.NewGuid();
    private static readonly Guid OtherShow = Guid.NewGuid();
    private static readonly DateTime Day = new(2026, 10, 27, 20, 0, 0, DateTimeKind.Utc);

    private static Candidate InProgress(Guid seriesId, int minutesAgo)
        => new(new MergedEntry(Guid.NewGuid(), BaseItemKind.Episode, false, Day.AddMinutes(-minutesAgo)), seriesId, Day.AddMinutes(-minutesAgo));

    private static Candidate NextUp(Guid seriesId, int lastFinishedMinutesAgo)
        => new(new MergedEntry(Guid.NewGuid(), BaseItemKind.Episode, true, Day.AddMinutes(-lastFinishedMinutesAgo)), seriesId, Day.AddMinutes(-lastFinishedMinutesAgo));

    private static Candidate Movie(int minutesAgo)
        => new(new MergedEntry(Guid.NewGuid(), BaseItemKind.Movie, false, Day.AddMinutes(-minutesAgo)), Guid.Empty, Day.AddMinutes(-minutesAgo));

    [Fact]
    public void SeveralPartlyWatchedEpisodes_ShowOnlyTheMostRecentlyPlayed()
    {
        var e6 = InProgress(Akudama, 5);
        var others = new[] { InProgress(Akudama, 30), InProgress(Akudama, 60), InProgress(Akudama, 90) };

        var result = MergedListBuilder.KeepLatestActivityPerSeries(others.Append(e6)).ToList();

        Assert.Equal(new[] { e6.Entry }, result);
    }

    [Fact]
    public void FinishingAnEpisode_ShowsTheNextOneInsteadOfOlderPartlyWatchedEpisodes()
    {
        var olderPartial = InProgress(Akudama, 60);
        var next = NextUp(Akudama, lastFinishedMinutesAgo: 5);

        Assert.Equal(new[] { next.Entry }, MergedListBuilder.KeepLatestActivityPerSeries(new[] { olderPartial, next }));
    }

    [Fact]
    public void PlayingPartOfAnEpisodeAfterFinishingOne_ShowsThatEpisode()
    {
        var partial = InProgress(Akudama, 5);
        var next = NextUp(Akudama, lastFinishedMinutesAgo: 60);

        Assert.Equal(new[] { partial.Entry }, MergedListBuilder.KeepLatestActivityPerSeries(new[] { next, partial }));
    }

    [Fact]
    public void OnATie_ThePartlyWatchedEpisodeWins()
    {
        var partial = InProgress(Akudama, 10);
        var next = NextUp(Akudama, lastFinishedMinutesAgo: 10);

        Assert.Equal(new[] { partial.Entry }, MergedListBuilder.KeepLatestActivityPerSeries(new[] { next, partial }));
    }

    [Fact]
    public void DifferentShowsAndMovies_AreAllKept()
    {
        var candidates = new[] { InProgress(Akudama, 5), NextUp(OtherShow, 10), Movie(20), Movie(30) };

        Assert.Equal(4, MergedListBuilder.KeepLatestActivityPerSeries(candidates).Count());
    }
}
