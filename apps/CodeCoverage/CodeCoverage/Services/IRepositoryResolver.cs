using CodeCoverage.Entities;

namespace CodeCoverage.Services;

/// <summary>
/// The outcome of resolving an <c>owner/name</c> pair.
/// </summary>
/// <param name="Repository">The repository, or null when the name resolves to nothing we know.</param>
/// <param name="Redirect">
/// True when the caller asked by a name this repository no longer answers to. Page endpoints should
/// 301 to <see cref="Entities.Repository.FullName"/>; the badge endpoint deliberately should not —
/// a redirect on an image inside a README is a wasted round-trip through GitHub's image proxy.
/// </param>
public sealed record RepositoryResolution(Repository? Repository, bool Redirect)
{
    public static readonly RepositoryResolution None = new(null, false);
}

/// <summary>
/// Turns an <c>owner/name</c> from a URL into a repository document, including when the repository
/// has since been renamed or transferred.
/// <para>
/// This exists because <c>owner/name</c> is not an identity. It is a label GitHub lets people
/// change, while badge URLs live in READMEs and report links live in pull-request comments that
/// were posted months ago and are never edited. Five call sites each wrote
/// <c>FullName == $"{owner}/{name}"</c> and each broke the same way on a rename.
/// </para>
/// </summary>
public interface IRepositoryResolver
{
    /// <summary>
    /// Resolves <paramref name="owner"/>/<paramref name="name"/>, following renames and transfers.
    /// A live full name always wins over a remembered one, so a new repository occupying an old
    /// name shadows the alias rather than colliding with it.
    /// </summary>
    Task<RepositoryResolution> ResolveAsync(string owner, string name, CancellationToken cancellationToken = default);
}
