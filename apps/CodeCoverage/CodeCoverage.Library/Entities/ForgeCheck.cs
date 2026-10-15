using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.Entities;

/// <summary>
/// A check that has reported at least once on a repository: a check run or a commit status, by the name the merge
/// queue's exclusion lists match (merge-queue PRD §11, O38).
/// <para>
/// Embedded in <see cref="Repository.KnownChecks"/> rather than stored as its own document, and stored rather than
/// fetched when the picker opens (D107): no forge lists the checks a repository <em>can</em> produce, only those that
/// reported on a commit, and crawling every pull request's checks each time a manager opens a settings page would cost
/// seconds and rate limit. The precedent is <see cref="ProjectColumn"/>.
/// </para>
/// </summary>
[ValueObject]
public partial class ForgeCheck
{
    /// <summary>The newest checks a repository keeps; the oldest <see cref="LastSeenUtc"/> is dropped beyond it (D107).</summary>
    public const int MaxKnown = 200;

    /// <summary>
    /// How old <see cref="LastSeenUtc"/> may get before a new sighting is written again. Without it every check run
    /// delivery would rewrite the repository; with it, each name costs at most one write a day.
    /// </summary>
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(1);

    /// <summary>
    /// The check's name, which is also its identity: the picker's row id is what an exclusion list stores, and the list
    /// stores the name (D110).
    /// </summary>
    [ValueKey]
    public string Id { get; set; } = string.Empty;

    /// <summary>The name of the check run or the context of the commit status, as the forge reports it.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// A check run or a commit status; null when the source gave the name alone (the queue's expected-minimum read).
    /// </summary>
    public EForgeCheckKind? Kind { get; set; }

    /// <summary>The App that made the check run, when the forge says so.</summary>
    public string? AppSlug { get; set; }

    /// <summary>When the check last reported, as far as this app has written it down (at most <see cref="RefreshAfter"/> stale).</summary>
    public DateTime LastSeenUtc { get; set; }

    /// <summary>
    /// The sightings in <paramref name="reported"/> worth a write against <paramref name="known"/>: a name not known yet,
    /// one last seen <see cref="RefreshAfter"/> or longer ago, or one that now says what kind or App it is. One per name,
    /// the queue's own check left out.
    /// </summary>
    public static IReadOnlyList<ForgeCheck> Due(IEnumerable<ForgeCheck>? known, IEnumerable<ForgeCheck> reported, DateTime nowUtc)
    {
        var byName = new Dictionary<string, ForgeCheck>(StringComparer.Ordinal);
        foreach (var check in known ?? [])
            if (!string.IsNullOrEmpty(check?.Id)) byName.TryAdd(check.Id, check);

        var due = new Dictionary<string, ForgeCheck>(StringComparer.Ordinal);
        foreach (var check in reported)
        {
            var name = check.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name == Forge.CheckGate.QueueCheckName) continue;

            var sighting = new ForgeCheck { Id = name, Name = name, Kind = check.Kind, AppSlug = check.AppSlug, LastSeenUtc = nowUtc };
            if (due.TryGetValue(name, out var earlier))
            {
                earlier.Kind ??= sighting.Kind;
                earlier.AppSlug ??= sighting.AppSlug;
                continue;
            }

            if (!byName.TryGetValue(name, out var stored)
                || nowUtc - stored.LastSeenUtc >= RefreshAfter
                || (stored.Kind is null && sighting.Kind is not null)
                || (stored.AppSlug is null && sighting.AppSlug is not null))
            {
                due[name] = sighting;
            }
        }

        return [.. due.Values];
    }
}

/// <summary>What a forge calls the thing that reported: GitHub's check run or its older commit status.</summary>
public enum EForgeCheckKind
{
    CheckRun,
    Status,
}
