using System.Text;
using CodeCoverage.MergeQueue;
using CodeCoverage.Versioning.Graph;

namespace CodeCoverage.Onboarding;

/// <summary>
/// The default branch's tree with the onboarding's edits on top (#571 M10b): each feature's planner reads the repository
/// as the features before it left it, and plans only what is still missing.
/// </summary>
internal sealed class OverlayTree(IRepositoryTree inner) : IRepositoryTree
{
    /// <summary>The largest file compared against the original; a bigger one counts as changed.</summary>
    private const long MaxCompareBytes = 16 * 1024 * 1024;

    private readonly Dictionary<string, string?> writes = new(StringComparer.Ordinal);
    private Dictionary<string, TreeEntry>? entries;

    public IReadOnlyDictionary<string, TreeEntry> Entries => entries ??= BuildEntries();

    public Task<byte[]?> ReadAsync(string path, long maxBytes, CancellationToken cancellationToken = default)
    {
        if (!writes.TryGetValue(path, out var text))
            return inner.ReadAsync(path, maxBytes, cancellationToken);
        if (text is null)
            return Task.FromResult<byte[]?>(null);
        var bytes = Encoding.UTF8.GetBytes(text);
        return Task.FromResult(bytes.LongLength > maxBytes ? null : bytes);
    }

    public void Apply(GitWriterFile file)
    {
        writes[file.Path] = file.Delete ? null : file.Content;
        entries = null;
    }

    public void Write(string path, string text) => Apply(new GitWriterFile(path, text));

    /// <summary>What differs from the default branch, by path: an edit that ends where the file started is no change.</summary>
    public async Task<IReadOnlyList<GitWriterFile>> ChangesAsync(CancellationToken cancellationToken)
    {
        var changes = new List<GitWriterFile>();
        foreach (var (path, text) in writes.OrderBy(w => w.Key, StringComparer.Ordinal))
        {
            var original = inner.IsFile(path) ? await inner.ReadTextAsync(path, MaxCompareBytes, cancellationToken) : null;
            if (text is null)
            {
                if (inner.Entries.ContainsKey(path))
                    changes.Add(new GitWriterFile(path, null, Delete: true));
            }
            else if (!string.Equals(original, text.TrimStart('﻿'), StringComparison.Ordinal))
            {
                changes.Add(new GitWriterFile(path, text));
            }
        }
        return changes;
    }

    private Dictionary<string, TreeEntry> BuildEntries()
    {
        var result = new Dictionary<string, TreeEntry>(inner.Entries, StringComparer.Ordinal);
        foreach (var (path, text) in writes)
        {
            if (text is null)
                result.Remove(path);
            else
                result[path] = new TreeEntry(path, inner.Entries.TryGetValue(path, out var entry) && entry.IsFile ? entry.Mode : "100644", "overlay",
                    Encoding.UTF8.GetByteCount(text));
        }
        return result;
    }
}
