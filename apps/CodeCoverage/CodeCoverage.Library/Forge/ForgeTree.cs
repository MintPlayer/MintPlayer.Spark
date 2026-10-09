namespace CodeCoverage.Forge;

/// <summary>A recursive listing of the files in one commit, as the forge reports it.</summary>
/// <param name="CommitSha">
/// The commit the branch pointed at when listed. File content must be fetched at this sha, never at
/// the branch name, so every file comes from the same snapshot even if the branch moves mid-scan.
/// </param>
/// <param name="TreeSha">The root tree of that commit; equal trees have equal contents.</param>
/// <param name="Entries">Every file (blob) in the tree. Directories and submodules are not listed.</param>
/// <param name="Truncated">The forge cut the listing short, so <paramref name="Entries"/> is a subset.</param>
public sealed record ForgeTree(string CommitSha, string TreeSha, IReadOnlyList<ForgeTreeEntry> Entries, bool Truncated);

/// <summary>One file in a <see cref="ForgeTree"/>.</summary>
/// <param name="Path">Relative to the repository root, <c>/</c>-separated.</param>
/// <param name="Size">In bytes.</param>
public sealed record ForgeTreeEntry(string Path, long Size);
