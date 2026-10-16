using System.Text;
using CodeCoverage.Git;

namespace CodeCoverage.GitWriter.Jobs.Dependencies;

/// <summary>Read access to the commit an apply run was planned on (<c>expected_sha</c>): what the validators compare the
/// patch against. Reads only; nothing in the repository is ever executed.</summary>
public interface IBaseTree
{
    /// <summary>The entry's mode (<c>100644</c>, <c>100755</c>, <c>120000</c>, …), or null when the path is absent or
    /// is a directory.</summary>
    Task<string?> ModeAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>The blob's text decoded as strict UTF-8, or null when it is absent, larger than the cap or not UTF-8.</summary>
    Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IBaseTree"/> over git plumbing: <c>ls-tree</c> for the mode and <c>cat-file</c> for the blob, both on
/// <c>&lt;commit&gt;:&lt;path&gt;</c>. The writer passes <c>JobContext.GitAsync</c>; tests pass a fixture git.
/// </summary>
public sealed class GitBaseTree(Func<IReadOnlyList<string>, Task<GitResult>> git, string commit, long maxBlobBytes = GitBaseTree.DefaultMaxBlobBytes) : IBaseTree
{
    /// <summary>16 MiB: this repository's largest lockfile is well under it.</summary>
    public const long DefaultMaxBlobBytes = 16L * 1024 * 1024;

    public async Task<string?> ModeAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = await git(["ls-tree", "-z", "--full-tree", commit, "--", path]);
        if (!result.Succeeded)
            return null;
        foreach (var record in result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = record.IndexOf('\t');
            if (tab < 0 || record[(tab + 1)..] != path)
                continue;
            var fields = record[..tab].Split(' ');
            return fields.Length == 3 && fields[1] == "blob" ? fields[0] : null;
        }
        return null;
    }

    public async Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken = default)
    {
        var objectName = $"{commit}:{path}";
        var size = await git(["cat-file", "-s", objectName]);
        if (!size.Succeeded || !long.TryParse(size.StandardOutput.Trim(), out var bytes) || bytes > maxBlobBytes)
            return null;
        var blob = await git(["cat-file", "blob", objectName]);
        if (!blob.Succeeded)
            return null;
        // The runner decodes as UTF-8; a replacement character means the blob was not valid UTF-8.
        // A reader that detects byte-order marks drops a leading BOM; it is put back so line 1 compares as git wrote it.
        var text = blob.StandardOutput;
        if (text.Contains('�'))
            return null;
        var count = Encoding.UTF8.GetByteCount(text);
        return count == bytes ? text : count + 3 == bytes ? '﻿' + text : null;
    }
}
