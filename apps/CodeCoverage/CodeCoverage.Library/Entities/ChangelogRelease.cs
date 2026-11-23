using CodeCoverage.Forge;
using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.Entities;

/// <summary>
/// One released version's changelog (docs/coverage_changelog_PRD.md D3, D16; plan M11): the notes of a package's Release,
/// copied for the repository's changelog page.
/// </summary>
/// <remarks>
/// <para>
/// One document per version tag, <c>ChangelogReleases/{provider}/{repoId}/{tag}</c>, written by <c>ChangelogRecipient</c>
/// from the forge's <c>release</c> webhook through the storage-agnostic <c>ISparkSession</c> (D16): deterministic ids, loads
/// by id and id-prefix reads, no index. The webhook is the source because both hosts create the Releases, and the action's
/// local mode reports nothing else to the server. An edit of the Release updates the copy (D3: corrections edit only
/// the Release); the tag's frozen notes stay as they were.
/// </para>
/// <para>
/// A package that moved only with a dependency has no Release of its own (D7), so it has no entry here; the run's
/// Release, whose tag is not a version tag, is not copied either.
/// </para>
/// <para>A read-only Spark entity: the repository page lists it through <c>Custom.Repository_Changelog</c>.</para>
/// </remarks>
public class ChangelogRelease
{
    public const string Prefix = "ChangelogReleases/";

    /// <summary>GitHub caps a Release body at 125,000 characters; a longer one is cut, not refused.</summary>
    public const int MaxNotes = 125_000;

    public static string DocumentId(EForgeProvider provider, long repositoryId, string tag) => $"{ForRepository(provider, repositoryId)}{tag}";

    /// <summary>The id prefix every release of one repository shares.</summary>
    public static string ForRepository(EForgeProvider provider, long repositoryId)
        => $"{Prefix}{provider.ToString().ToLowerInvariant()}/{repositoryId}/";

    public string? Id { get; set; }

    /// <summary>The repository the package was released from.</summary>
    [Reference(typeof(Repository))]
    public string Repository { get; set; } = string.Empty;

    /// <summary>The package id.</summary>
    public string Package { get; set; } = string.Empty;

    /// <summary>The version released.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>The version tag; the Release carries the same name.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>The branch or commit the Release names.</summary>
    public string? Target { get; set; }

    /// <summary>When the Release was published.</summary>
    public DateTime ReleasedAtUtc { get; set; }

    /// <summary>A prerelease version.</summary>
    public bool Prerelease { get; set; }

    /// <summary>The release notes, as markdown.</summary>
    public string Notes { get; set; } = string.Empty;
}
