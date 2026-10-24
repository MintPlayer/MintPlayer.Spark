using System.Text;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Versioning.Graph;

namespace CodeCoverage.PackageVersioning;

/// <summary>
/// A branch head read through the forge's API, for the onboarding plan (D17): the app has no mirror of a repository it
/// has not versioned yet. Files are fetched on first read and kept.
/// </summary>
public sealed class ForgeRepositoryTree : IRepositoryTree
{
    private readonly IForgeIntegration forge;
    private readonly Repository repository;
    private readonly Dictionary<string, byte[]?> read = new(StringComparer.Ordinal);

    private ForgeRepositoryTree(IForgeIntegration forge, Repository repository, ForgeTree tree)
    {
        this.forge = forge;
        this.repository = repository;
        Commit = tree.CommitSha;
        Entries = tree.Entries.ToDictionary(e => e.Path, e => new TreeEntry(e.Path, "100644", "", e.Size), StringComparer.Ordinal);
    }

    public string Commit { get; }

    public IReadOnlyDictionary<string, TreeEntry> Entries { get; }

    /// <summary>The branch's head, or null when the branch is absent; a listing the forge truncated is refused.</summary>
    public static async Task<ForgeRepositoryTree?> OpenAsync(IForgeIntegration forge, Repository repository, string branch, CancellationToken cancellationToken = default)
    {
        if (await forge.GetTreeAsync(repository, branch, cancellationToken) is not { } tree) return null;
        if (tree.Truncated) throw new GraphException($"The forge truncated the file listing of {branch}: the repository is too large to plan its onboarding.");
        if (tree.Entries.Count > Limits.MaxTreeEntries) throw new GraphException($"The repository has more than {Limits.MaxTreeEntries} files on {branch}.");
        return new ForgeRepositoryTree(forge, repository, tree);
    }

    public async Task<byte[]?> ReadAsync(string path, long maxBytes, CancellationToken cancellationToken = default)
    {
        if (!Entries.TryGetValue(path, out var entry)) return null;
        if (entry.Size > maxBytes) throw new GraphException($"{path} is {entry.Size} bytes, over the {maxBytes}-byte limit for this kind of file.");
        if (!read.TryGetValue(path, out var bytes))
            read[path] = bytes = await forge.GetFileContentAsync(repository, Commit, path, cancellationToken) is { } text ? Encoding.UTF8.GetBytes(text) : null;
        return bytes;
    }
}
