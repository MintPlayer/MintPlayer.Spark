namespace MintPlayer.Spark.Abstractions.Storage;

/// <summary>
/// RavenDB's retention rule, for the stores that keep revisions themselves (#536 S8): a revision goes once it is beyond
/// <see cref="SparkRevisionSettings.MinimumRevisionsToKeep"/> AND older than
/// <see cref="SparkRevisionSettings.MinimumRevisionAgeToKeep"/>; with one unset the other decides alone, and with both unset
/// every revision stays.
/// </summary>
public static class SparkRevisionRetention
{
    /// <summary>The positions in <paramref name="newestFirst"/> (write times, newest first) that go.</summary>
    public static IEnumerable<int> Expired(IReadOnlyList<DateTimeOffset> newestFirst, SparkRevisionSettings settings, DateTimeOffset now)
    {
        if (settings.MinimumRevisionsToKeep is null && settings.MinimumRevisionAgeToKeep is null)
            yield break;
        for (var i = 0; i < newestFirst.Count; i++)
        {
            var beyondCount = settings.MinimumRevisionsToKeep is not { } keep || i >= keep;
            var tooOld = settings.MinimumRevisionAgeToKeep is not { } age || now - newestFirst[i] > age;
            if (beyondCount && tooOld)
                yield return i;
        }
    }
}
