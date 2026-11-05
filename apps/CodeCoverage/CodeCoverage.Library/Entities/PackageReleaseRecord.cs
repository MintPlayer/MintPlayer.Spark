namespace CodeCoverage.Entities;

/// <summary>
/// Which repository on this instance releases a package, and what each release brought (#570 D11): the lookup a backport
/// uses to point at the upstream release a raised first-party dependency needs. One document per package,
/// <c>PackageReleases/{kind}/{id}</c>, stored and loaded by id (no query, so no RQL to splice into), written as a
/// versioning run records its tags.
/// </summary>
/// <remarks>
/// The first repository to release a package id keeps it: another repository's release of the same id is not recorded,
/// so a repository cannot take over a name another one already releases. A package released before this record existed
/// is unknown until its next release, and a backport then names the dependency only.
/// </remarks>
public class PackageReleaseRecord
{
    public const string Prefix = "PackageReleases/";

    /// <summary>The releases kept per package, newest last.</summary>
    public const int MaxReleases = 200;

    /// <summary>The pull requests kept per release, from its notes.</summary>
    public const int MaxPullRequests = 50;

    /// <summary><paramref name="kind"/> is <c>npm</c> or <c>nuget</c>, as the versioning engine names package kinds; package
    /// ids compare case-insensitively on both registries.</summary>
    public static string DocumentId(string kind, string package) => $"{Prefix}{kind.ToLowerInvariant()}/{package.ToLowerInvariant()}";

    public string? Id { get; set; }

    /// <summary>The <see cref="Entities.Repository"/> document id that releases the package.</summary>
    public string Repository { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string Package { get; set; } = string.Empty;

    public List<PackageReleaseEntry> Releases { get; set; } = [];
}

public sealed class PackageReleaseEntry
{
    public string Version { get; set; } = string.Empty;

    public string Tag { get; set; } = string.Empty;

    /// <summary>The pull requests the release notes name (<c>(#N)</c>).</summary>
    public List<int> PullRequests { get; set; } = [];
}
