using System.Text;
using MintPlayer.Spark.Replication.Abstractions.Rendering;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>
/// The retry ladder of replication's messages (warehouse §4.2.3: the reference system's 1 m / 5 m / 30 m / 2 h, the
/// last step repeating), derived from the time since the first attempt: no counter has to be stored, and a
/// <c>RetryAfterException</c> park does not count as a messaging attempt.
/// </summary>
internal static class RetryLadder
{
    /// <summary>The wait before the next attempt, <paramref name="elapsed"/> after the first one.</summary>
    public static TimeSpan Next(IReadOnlyList<TimeSpan> ladder, TimeSpan elapsed)
    {
        if (ladder.Count == 0)
            return TimeSpan.FromMinutes(1);

        var sum = TimeSpan.Zero;
        foreach (var step in ladder)
        {
            sum += step;
            if (sum > elapsed)
                return step;
        }
        return ladder[^1];
    }

    /// <summary>
    /// Attempts a retrying message may need within <paramref name="giveUpAfter"/>, as a messaging budget: every park is
    /// a rate-limit park, five of which make one attempt by default, so this is generous on purpose — the recipient
    /// itself decides when to give up.
    /// </summary>
    public static int Budget(IReadOnlyList<TimeSpan> ladder, TimeSpan giveUpAfter)
    {
        var parks = 0;
        var elapsed = TimeSpan.Zero;
        while (elapsed < giveUpAfter && parks < 10_000)
        {
            elapsed += Next(ladder, elapsed);
            parks++;
        }
        return Math.Max(5, parks + 5);
    }
}

/// <summary>
/// The readable difference between two manifests, for the administrator deciding a replication request: fields added,
/// removed or read from another path, and a changed source or target collection.
/// </summary>
internal static class ManifestDiff
{
    public static string Describe(ReplicaManifest? approved, ReplicaManifest requested)
    {
        var text = new StringBuilder();
        if (approved is null)
        {
            text.Append("New request: ").Append(requested.SourceCollection).Append(" → ").Append(requested.TargetCollection).Append('\n');
            foreach (var field in requested.Fields)
                text.Append("+ ").Append(Field(field)).Append('\n');
            return text.ToString().TrimEnd('\n');
        }

        if (approved.SourceCollection != requested.SourceCollection)
            text.Append("Source collection: ").Append(approved.SourceCollection).Append(" → ").Append(requested.SourceCollection).Append('\n');
        if (approved.TargetCollection != requested.TargetCollection)
            text.Append("Target collection: ").Append(approved.TargetCollection).Append(" → ").Append(requested.TargetCollection).Append('\n');

        var before = approved.Fields.ToDictionary(f => f.Target, StringComparer.Ordinal);
        var after = requested.Fields.ToDictionary(f => f.Target, StringComparer.Ordinal);
        foreach (var field in requested.Fields)
        {
            if (!before.TryGetValue(field.Target, out var old))
                text.Append("+ ").Append(Field(field)).Append('\n');
            else if (old.PathText != field.PathText)
                text.Append("~ ").Append(Field(field)).Append(" (was ").Append(old.PathText).Append(")\n");
        }
        foreach (var field in approved.Fields.Where(f => !after.ContainsKey(f.Target)))
            text.Append("- ").Append(Field(field)).Append('\n');

        return text.Length == 0 ? "No field changes (the script or hash format changed)." : text.ToString().TrimEnd('\n');
    }

    private static string Field(ReplicaField field)
        => field.PathText == field.Target ? field.Target : $"{field.Target} ← {field.PathText}";
}
