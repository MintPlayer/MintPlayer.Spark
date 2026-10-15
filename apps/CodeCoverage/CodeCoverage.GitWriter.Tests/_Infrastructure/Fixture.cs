using System.Diagnostics;
using System.Text;

namespace CodeCoverage.GitWriter.Tests._Infrastructure;

/// <summary>A temp directory deleted with its read-only pack files.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir(string label)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitwriter-tests", $"{label}-{Guid.NewGuid():N}"[..(label.Length + 13)]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose() => Jobs.JobRunner.DeleteTree(Path);
}

/// <summary>
/// git for building fixtures, NOT the code under test: a hermetic environment (no system or user config, no inherited
/// GIT_* variables, LF kept byte-exact) so the machine's git config cannot change a fixture or a SHA.
/// </summary>
internal static class FixtureGit
{
    private static readonly Lazy<string> EmptyConfig = new(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), "gitwriter-tests-empty.gitconfig");
        if (!File.Exists(path))
            File.WriteAllBytes(path, []);
        return path;
    });

    public static ProcessStartInfo StartInfo(string? dir, IEnumerable<string> args, IReadOnlyDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToList())
            psi.Environment.Remove(key);
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_CONFIG_GLOBAL"] = EmptyConfig.Value;
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var (key, value) in env ?? new Dictionary<string, string>())
            psi.Environment[key] = value;
        if (dir is not null)
        {
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(dir);
        }
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.autocrlf=false");
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        return psi;
    }

    public static string Run(string? dir, params string[] args) => RunWith(dir, null, args);

    public static string RunWith(string? dir, IReadOnlyDictionary<string, string>? env, params string[] args)
    {
        var (code, output, error) = TryRun(dir, env, args);
        return code == 0 ? output : throw new InvalidOperationException($"git {string.Join(' ', args)} exited {code}: {error}");
    }

    public static (int Code, string Output, string Error) TryRun(string? dir, IReadOnlyDictionary<string, string>? env, params string[] args)
    {
        using var process = Process.Start(StartInfo(dir, args, env))!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardInput.Close();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result, stderr.Result);
    }
}

/// <summary>
/// A forge repository: a work tree to commit in, pushed into a bare repository the <see cref="GitHttpServer"/> serves. The
/// served repository is configured like GitHub as far as the writer can tell: partial clone allowed, any SHA fetchable
/// (lazy blob fetches), receive-pack on, and <c>refs/pull/*</c> hidden from pushes (a fork's head is reachable only as
/// <c>refs/pull/N/head</c>, so a push must send the pull request's blobs, which a blob-less clone fetches lazily).
/// </summary>
internal sealed class Origin : IDisposable
{
    /// <summary>2026-01-01T00:00:00Z; each commit is one minute later than the previous one.</summary>
    private const long Epoch = 1767225600;

    private readonly TempDir work = new("work");
    private int commits;

    public Origin(GitHttpServer server, string name)
    {
        Server = server;
        Name = name;
        Bare = server.PathOf(name);
        FixtureGit.Run(null, "init", "--quiet", "--bare", "--initial-branch=main", Bare);
        foreach (var (key, value) in new[] { ("http.receivepack", "true"), ("uploadpack.allowFilter", "true"), ("uploadpack.allowAnySHA1InWant", "true"), ("receive.hideRefs", "refs/pull") })
            FixtureGit.Run(Bare, "config", key, value);
        FixtureGit.Run(null, "init", "--quiet", "--initial-branch=main", work.Path);
        FixtureGit.Run(work.Path, "remote", "add", "origin", Bare);
    }

    public GitHttpServer Server { get; }
    public string Name { get; }
    public string Bare { get; }
    public string Work => work.Path;
    public Uri Url => Server.UrlOf(Name);

    public Origin Write(string path, string content)
    {
        var full = Path.Combine(Work, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new UTF8Encoding(false).GetBytes(content));
        return this;
    }

    public string Commit(string message, string author = "Fixture Author", string email = "author@example.invalid")
    {
        FixtureGit.Run(Work, "add", "--all");
        var date = $"{Epoch + 60 * ++commits} +0000";
        FixtureGit.RunWith(Work, new Dictionary<string, string> { ["GIT_AUTHOR_DATE"] = date, ["GIT_COMMITTER_DATE"] = date },
            "-c", $"user.name={author}", "-c", $"user.email={email}", "commit", "--quiet", "--allow-empty", "-m", message);
        return Head();
    }

    public string Head() => FixtureGit.Run(Work, "rev-parse", "HEAD").Trim();

    public Origin Checkout(string branch, string? from = null)
    {
        if (from is null)
            FixtureGit.Run(Work, "checkout", "--quiet", branch);
        else
            FixtureGit.Run(Work, "checkout", "--quiet", "-b", branch, from);
        return this;
    }

    /// <summary>Force-pushes a work branch to the served repository.</summary>
    public Origin Push(string branch)
    {
        FixtureGit.Run(Work, "push", "--quiet", "--force", "origin", $"{branch}:refs/heads/{branch}");
        return this;
    }

    /// <summary>Points <c>refs/pull/{number}/head</c> at a work branch's commit, as GitHub does for every pull request.</summary>
    public Origin PullRequest(int number, string branch)
    {
        var sha = FixtureGit.Run(Work, "rev-parse", branch).Trim();
        FixtureGit.Run(Work, "push", "--quiet", "origin", $"{sha}:refs/heads/tmp-{number}");
        FixtureGit.Run(Bare, "update-ref", $"refs/pull/{number}/head", sha);
        FixtureGit.Run(Bare, "update-ref", "-d", $"refs/heads/tmp-{number}");
        return this;
    }

    /// <summary>A ref of the served repository, or null when it does not exist.</summary>
    public string? Ref(string name)
    {
        var (code, output, _) = FixtureGit.TryRun(Bare, null, "rev-parse", "--verify", "--quiet", name);
        return code == 0 ? output.Trim() : null;
    }

    /// <summary>git in the served repository.</summary>
    public string InBare(params string[] args) => FixtureGit.Run(Bare, args);

    /// <summary>
    /// Writes a copy of commit <paramref name="sha"/> into the served repository with its raw text changed by
    /// <paramref name="edit"/> (a header line swapped, a date moved), and returns the copy's id. Nothing points at it.
    /// </summary>
    public string Rewrite(string sha, Func<string, string> edit)
    {
        var path = Path.Combine(Work, ".git", $"rewrite-{Guid.NewGuid():N}");
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(edit(InBare("cat-file", "commit", sha))));
        try
        {
            return InBare("hash-object", "-t", "commit", "-w", path).Trim();
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Points branch <paramref name="branch"/> of the served repository at <paramref name="sha"/>, as a push would.</summary>
    public Origin SetBranch(string branch, string sha)
    {
        InBare("update-ref", $"refs/heads/{branch}", sha);
        return this;
    }

    public void Dispose() => work.Dispose();
}

/// <summary>Edits of a commit object's raw text, for <see cref="Origin.Rewrite"/>.</summary>
internal static class CommitText
{
    /// <summary>The same commit, committed at another time. Since §0 D65 pins every date, that is never what an earlier
    /// attempt of the same job wrote: it is someone else's commit.</summary>
    public static string Redated(string raw)
        => Header(raw, "committer", line => line[..(line.LastIndexOf('>') + 1)] + " 1767225600 +0000");

    public static string Header(string raw, string name, Func<string, string> edit)
    {
        var split = raw.IndexOf("\n\n", StringComparison.Ordinal);
        var lines = raw[..split].Split('\n');
        var index = Array.FindIndex(lines, l => l.StartsWith(name + " ", StringComparison.Ordinal));
        if (index < 0)
            throw new InvalidOperationException($"The commit has no {name} header.");
        lines[index] = edit(lines[index]);
        return string.Join('\n', lines) + raw[split..];
    }
}

/// <summary>Spark's <c>App_Data/modelHashes.json</c> layout (two-space indent, LF, roll-ups first). Fixture values.</summary>
internal static class ModelHashes
{
    public const string Path = "apps/DemoApp/DemoApp/App_Data/modelHashes.json";

    public static readonly string[] BaseEntities = ["Address", "Motorcycle", "Person"];

    public static string Render(string rollUp, IEnumerable<string> entities)
    {
        var names = entities.Order(StringComparer.Ordinal).ToList();
        var builder = new StringBuilder();
        builder.Append("{\n");
        builder.Append("  \"version\": 2,\n");
        builder.Append($"  \"modelHash\": \"{Hash("model", rollUp)}\",\n");
        builder.Append($"  \"contextRoots\": \"{Hash("roots", rollUp)}\",\n");
        builder.Append($"  \"modelFiles\": \"{Hash("files", rollUp)}\",\n");
        builder.Append("  \"files\": {\n");
        for (var i = 0; i < names.Count; i++)
            builder.Append($"    \"{names[i]}.json\": \"{Hash("file", names[i])}\"{(i < names.Count - 1 ? "," : "")}\n");
        builder.Append("  },\n");
        builder.Append("  \"entities\": {\n");
        for (var i = 0; i < names.Count; i++)
            builder.Append($"    \"{names[i]}\": \"{Hash("entity", names[i])}\"{(i < names.Count - 1 ? "," : "")}\n");
        builder.Append("  }\n");
        builder.Append("}\n");
        return builder.ToString();
    }

    public static string Hash(string kind, string seed)
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(kind + ":" + seed)));
}
