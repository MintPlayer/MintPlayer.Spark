using CodeCoverage.Entities;
using CodeCoverage.Forge;

namespace CodeCoverage.CommitGraph;

/// <summary>
/// Step 4 of the avatar resolution order (D28): ask the forge, in one batched call, which account
/// authored each of a page's commits whose author is not cached yet.
/// </summary>
/// <remarks>
/// <para>
/// Optional per forge. A forge with no implementation registered resolves nobody, and its authors
/// get initials. Selected by <see cref="Provider"/>, so no caller names a forge.
/// </para>
/// <para>
/// ⚠️ The emails stay on the server. They are the lookup key, never part of what the forge is
/// asked: the question is "who authored this SHA", which needs no email at all.
/// </para>
/// </remarks>
public interface IForgeAvatarResolver
{
    EForgeProvider Provider { get; }

    /// <summary>
    /// The avatar URL per email, null for an author the forge knows no account for. Null overall
    /// when the forge could not be asked (no installation, an error): nothing is then cached, so a
    /// later page asks again.
    /// </summary>
    Task<IReadOnlyDictionary<string, string?>?> ResolveAsync(
        Repository repository, IReadOnlyList<AvatarLookup> lookups, CancellationToken cancellationToken = default);
}

/// <summary>One author to resolve: an email and one commit SHA that author wrote.</summary>
public sealed record AvatarLookup(string Email, string Sha);
