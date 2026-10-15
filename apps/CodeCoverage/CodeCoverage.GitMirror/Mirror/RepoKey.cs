using System.Text.RegularExpressions;

namespace CodeCoverage.GitMirror.Mirror;

/// <summary>A repository's stable key: <c>{provider}/{repoId}</c>, both validated before they touch a path or an
/// argument (contract: provider <c>^[a-z]+$</c>, repoId <c>^[0-9A-Za-z_-]{1,64}$</c>).</summary>
public sealed partial record RepoKey(string Provider, string RepoId)
{
    [GeneratedRegex("^[a-z]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProviderPattern();

    [GeneratedRegex("^[0-9A-Za-z_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex RepoIdPattern();

    public static RepoKey? TryCreate(string? provider, string? repoId)
        => provider is not null && repoId is not null && ProviderPattern().IsMatch(provider) && RepoIdPattern().IsMatch(repoId)
            ? new RepoKey(provider, repoId)
            : null;

    public override string ToString() => $"{Provider}/{RepoId}";
}

/// <summary>Where mirrors live: <c>{Root}/{provider}/{repoId}.git</c>, clones in progress beside them as
/// <c>{repoId}.git.partial-{n}</c> so a reader never sees a half-cloned mirror.</summary>
public sealed class MirrorPaths(string root)
{
    public const string PartialMarker = ".git.partial-";

    public string Root { get; } = Path.GetFullPath(root);

    public string Mirror(RepoKey key) => Path.Combine(Root, key.Provider, key.RepoId + ".git");

    public string NewPartial(RepoKey key) => Path.Combine(Root, key.Provider, key.RepoId + PartialMarker + Guid.NewGuid().ToString("N"));

    public IEnumerable<string> Partials(RepoKey key)
    {
        var dir = Path.Combine(Root, key.Provider);
        return Directory.Exists(dir) ? Directory.EnumerateDirectories(dir, key.RepoId + PartialMarker + "*") : [];
    }

    /// <summary>Whether a complete mirror exists (a bare repository has a HEAD file).</summary>
    public bool Exists(RepoKey key) => File.Exists(Path.Combine(Mirror(key), "HEAD"));

    /// <summary>Every complete mirror under the root.</summary>
    public IEnumerable<string> AllMirrors()
        => Directory.Exists(Root)
            ? Directory.EnumerateDirectories(Root).SelectMany(p => Directory.EnumerateDirectories(p, "*.git")).Where(d => File.Exists(Path.Combine(d, "HEAD")))
            : [];

    /// <summary>A directory's size on disk: the sum of its files' lengths (what <c>du --apparent-size</c> reports).</summary>
    public static long SizeOf(string directory)
        => Directory.Exists(directory)
            ? new DirectoryInfo(directory).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true }).Sum(f => f.Length)
            : 0;

    /// <summary>Renames a finished clone into place and returns how many attempts failed first. On Windows a directory
    /// rename fails with "Access to the path is denied" while any process holds a handle on a file inside it without
    /// FILE_SHARE_DELETE; an on-access virus scanner opening the pack git has just written does exactly that for a
    /// moment (docs/coverage_merge_queue_PRD.md D26: two of 14 full test runs failed this way at line 207, never on
    /// Linux, where a rename ignores open handles). Retried for up to 2 s, like <see cref="DeleteTree"/>.</summary>
    public static int MoveTree(string source, string destination)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return attempt;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 20 && Directory.Exists(source) && !Directory.Exists(destination))
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>Deletes a directory tree, clearing the read-only attribute git puts on pack files (Windows refuses to
    /// delete those otherwise). A missing directory is not an error.</summary>
    public static void DeleteTree(string directory)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (!Directory.Exists(directory))
                return;
            try
            {
                foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                {
                    if (file.Attributes.HasFlag(FileAttributes.ReadOnly))
                        file.Attributes &= ~FileAttributes.ReadOnly;
                }
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 20)
            {
                // A killed process's handles close asynchronously on Windows.
                Thread.Sleep(100);
            }
        }
    }
}
