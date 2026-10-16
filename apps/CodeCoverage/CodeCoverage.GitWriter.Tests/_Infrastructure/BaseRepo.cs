using System.Text;
using CodeCoverage.Git;
using CodeCoverage.GitWriter.Jobs.Dependencies;

namespace CodeCoverage.GitWriter.Tests._Infrastructure;

/// <summary>
/// A bare repository whose commits are built entry by entry with git plumbing (hash-object, update-index, write-tree,
/// commit-tree), so a test controls every path, mode and byte; the dependency validators read it through the real
/// <see cref="GitBaseTree"/>. Patches between two commits are written by real <c>git diff --binary</c>.
/// </summary>
internal sealed class BaseRepo : IDisposable
{
    private readonly TempDir dir = new("base");
    private int indexes;
    private int commits;

    public BaseRepo()
    {
        Repo = Path.Combine(dir.Path, "base.git");
        FixtureGit.Run(null, "init", "--quiet", "--bare", Repo);
    }

    public string Repo { get; }

    public void Dispose() => dir.Dispose();

    /// <summary>A root commit holding exactly these entries (mode 100644 unless given; 120000's content is the target).</summary>
    public string Commit(IEnumerable<(string Path, string Mode, byte[] Content)> entries, string? parent = null)
    {
        var index = Path.Combine(dir.Path, $"{++indexes}.index");
        var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = index };
        foreach (var (path, mode, content) in entries)
        {
            var file = Path.Combine(dir.Path, $"blob-{Guid.NewGuid():N}");
            File.WriteAllBytes(file, content);
            var oid = FixtureGit.Run(Repo, "hash-object", "-w", "--no-filters", file).Trim();
            FixtureGit.RunWith(Repo, env, "update-index", "--add", "--cacheinfo", $"{mode},{oid},{path}");
        }
        var tree = FixtureGit.RunWith(Repo, env, "write-tree").Trim();
        var date = $"{1767225600 + 60 * ++commits} +0000";
        var dates = new Dictionary<string, string> { ["GIT_AUTHOR_DATE"] = date, ["GIT_COMMITTER_DATE"] = date };
        string[] args = parent is null ? ["commit-tree", tree, "-m", $"commit {commits}"] : ["commit-tree", tree, "-p", parent, "-m", $"commit {commits}"];
        return FixtureGit.RunWith(Repo, dates, ["-c", "user.name=Fixture Author", "-c", "user.email=author@example.invalid", .. args]).Trim();
    }

    public string Commit(params (string Path, string Content)[] files)
        => Commit(files.Select(f => (f.Path, "100644", Encoding.UTF8.GetBytes(f.Content))));

    /// <summary>A commit of a fixture's base directory; a leading '_' in a directory name stands for '.'.</summary>
    public string CommitDirectory(string directory)
        => Commit(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(file => (string.Join('/', Path.GetRelativePath(directory, file).Split(Path.DirectorySeparatorChar)
                    .Select(part => part.StartsWith('_') ? "." + part[1..] : part)),
                "100644", File.ReadAllBytes(file))));

    /// <summary><c>git diff --binary</c> between two commits, as the apply workflow uploads it.</summary>
    public byte[] Diff(string from, string to) => Encoding.UTF8.GetBytes(FixtureGit.Run(Repo, "diff", "--binary", from, to));

    /// <summary>The commit holding <paramref name="from"/>'s tree with <paramref name="changes"/> written over it
    /// (null deletes the path).</summary>
    public string Change(string from, params (string Path, string? Content)[] changes)
    {
        var entries = FixtureGit.Run(Repo, "ls-tree", "-r", "-z", "--full-tree", from).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(record =>
            {
                var tab = record.IndexOf('\t');
                var meta = record[..tab].Split(' ');
                return (Path: record[(tab + 1)..], Mode: meta[0], Oid: meta[2]);
            })
            .ToDictionary(e => e.Path, e => (e.Mode, Content: Blob(e.Oid)), StringComparer.Ordinal);
        foreach (var (path, content) in changes)
        {
            if (content is null)
                entries.Remove(path);
            else
                entries[path] = ("100644", Encoding.UTF8.GetBytes(content));
        }
        return Commit(entries.Select(e => (e.Key, e.Value.Mode, e.Value.Content)));
    }

    /// <summary>The text of <paramref name="path"/> at <paramref name="commit"/>.</summary>
    public string Read(string commit, string path) => FixtureGit.Run(Repo, "cat-file", "blob", $"{commit}:{path}");

    private byte[] Blob(string oid)
    {
        var file = Path.Combine(dir.Path, $"out-{Guid.NewGuid():N}");
        using (var process = System.Diagnostics.Process.Start(FixtureGit.StartInfo(Repo, ["cat-file", "blob", oid]))!)
        {
            using var output = File.Create(file);
            process.StandardOutput.BaseStream.CopyTo(output);
            process.WaitForExit();
        }
        return File.ReadAllBytes(file);
    }

    public IBaseTree Tree(string commit) => new GitBaseTree(Git, commit);

    /// <summary>git over this repository, shaped like the writer's <c>JobContext.GitAsync</c>.</summary>
    public Task<GitResult> Git(IReadOnlyList<string> arguments)
    {
        var (code, output, error) = FixtureGit.TryRun(Repo, null, [.. arguments]);
        return Task.FromResult(new GitResult(code, output, error, false));
    }

    /// <summary>Whether real <c>git apply --cached --check</c> accepts the patch on the commit's tree.</summary>
    public bool Applies(string commit, byte[] patch)
    {
        var index = Path.Combine(dir.Path, $"{++indexes}.index");
        var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = index };
        FixtureGit.RunWith(Repo, env, "read-tree", commit);
        var file = Path.Combine(dir.Path, $"patch-{Guid.NewGuid():N}.diff");
        File.WriteAllBytes(file, patch);
        return FixtureGit.TryRun(Repo, env, "apply", "--cached", "--check", file).Code == 0;
    }

    /// <summary>The fixture directory <c>Fixtures/&lt;parts&gt;</c> copied next to the test assembly.</summary>
    public static string Fixture(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "Fixtures", .. parts]);
}
