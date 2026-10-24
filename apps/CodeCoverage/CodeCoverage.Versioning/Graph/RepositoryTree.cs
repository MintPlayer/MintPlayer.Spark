using System.Globalization;
using System.Text;
using CodeCoverage.Versioning.Git;

namespace CodeCoverage.Versioning.Graph;

/// <summary>One entry of a commit's tree. <see cref="IsSymlink"/> entries (mode 120000) are never read (PRD D5).</summary>
public sealed record TreeEntry(string Path, string Mode, string ObjectId, long Size)
{
	public bool IsFile => Mode is "100644" or "100755";
	public bool IsSymlink => Mode == "120000";
}

/// <summary>
/// The files of one commit, read without a checkout: the server reads a bare mirror and must never materialise a
/// consumer's repository on disk. Paths use <c>/</c> and are relative to the repository root.
/// </summary>
public interface IRepositoryTree
{
	IReadOnlyDictionary<string, TreeEntry> Entries { get; }

	/// <summary>The file's bytes, or null when it is absent, not a regular file, or larger than <paramref name="maxBytes"/>.</summary>
	Task<byte[]?> ReadAsync(string path, long maxBytes, CancellationToken cancellationToken = default);
}

public static class RepositoryTreeExtensions
{
	public static async Task<string?> ReadTextAsync(this IRepositoryTree tree, string path, long maxBytes, CancellationToken cancellationToken = default)
		=> await tree.ReadAsync(path, maxBytes, cancellationToken) is { } bytes ? Encoding.UTF8.GetString(bytes).TrimStart('﻿') : null;

	public static bool IsFile(this IRepositoryTree tree, string path) => tree.Entries.TryGetValue(path, out var entry) && entry.IsFile;
}

/// <summary>A commit's tree in a git repository, through <c>ls-tree</c> and <c>cat-file</c>.</summary>
public sealed class GitTree : IRepositoryTree
{
	private readonly GitCli git;

	private GitTree(GitCli git, string commit, Dictionary<string, TreeEntry> entries)
	{
		this.git = git;
		Commit = commit;
		Entries = entries;
	}

	public string Commit { get; }

	public IReadOnlyDictionary<string, TreeEntry> Entries { get; }

	public static async Task<GitTree> OpenAsync(GitCli git, string commit, CancellationToken cancellationToken = default)
	{
		GitCli.RequireObjectId(commit);
		var listing = (await git.RunAsync(["ls-tree", "-r", "-l", "-z", "--full-tree", commit], cancellationToken: cancellationToken)).EnsureSuccess("ls-tree");
		var entries = new Dictionary<string, TreeEntry>(StringComparer.Ordinal);
		foreach (var record in listing.Text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
		{
			// "<mode> <type> <object> <size, padded>\t<path>"
			var tab = record.IndexOf('\t');
			if (tab < 0) continue;
			var fields = record[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (fields.Length != 4) continue;
			var size = long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : 0;
			entries[record[(tab + 1)..]] = new TreeEntry(record[(tab + 1)..], fields[0], fields[2], size);
			if (entries.Count > Limits.MaxTreeEntries)
				throw new GraphException($"The repository has more than {Limits.MaxTreeEntries} files at {commit}.");
		}
		return new GitTree(git, commit, entries);
	}

	public async Task<byte[]?> ReadAsync(string path, long maxBytes, CancellationToken cancellationToken = default)
	{
		if (!Entries.TryGetValue(path, out var entry) || !entry.IsFile) return null;
		if (entry.Size > maxBytes) throw new GraphException($"{path} is {entry.Size} bytes, over the {maxBytes}-byte limit for this kind of file.");
		var blob = (await git.RunAsync(["cat-file", "blob", entry.ObjectId], cancellationToken: cancellationToken)).EnsureSuccess("cat-file");
		return blob.Output;
	}
}

/// <summary>A tree held in memory: tests, and the onboarding PR's content checks.</summary>
public sealed class InMemoryTree : IRepositoryTree
{
	private readonly Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
	private readonly Dictionary<string, TreeEntry> entries = new(StringComparer.Ordinal);

	public IReadOnlyDictionary<string, TreeEntry> Entries => entries;

	public InMemoryTree Add(string path, string content) => Add(path, Encoding.UTF8.GetBytes(content));

	public InMemoryTree Add(string path, byte[] content)
	{
		files[path] = content;
		entries[path] = new TreeEntry(path, "100644", Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(content)).ToLowerInvariant(), content.Length);
		return this;
	}

	public InMemoryTree AddSymlink(string path, string target)
	{
		files[path] = Encoding.UTF8.GetBytes(target);
		entries[path] = new TreeEntry(path, "120000", new string('0', 40), target.Length);
		return this;
	}

	public Task<byte[]?> ReadAsync(string path, long maxBytes, CancellationToken cancellationToken = default)
	{
		if (!entries.TryGetValue(path, out var entry) || !entry.IsFile) return Task.FromResult<byte[]?>(null);
		if (entry.Size > maxBytes) throw new GraphException($"{path} is {entry.Size} bytes, over the {maxBytes}-byte limit for this kind of file.");
		return Task.FromResult<byte[]?>(files[path]);
	}
}

public sealed class GraphException(string message) : Exception(message);
