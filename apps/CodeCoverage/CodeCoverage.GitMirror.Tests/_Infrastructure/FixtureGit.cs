using System.Diagnostics;
using System.Text;
using CodeCoverage.GitMirror.Mirror;

namespace CodeCoverage.GitMirror.Tests._Infrastructure;

/// <summary>A temporary directory, deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir(string label)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitmirror-tests", $"{label}-{Guid.NewGuid():N}"[..Math.Min(label.Length + 13, 40)]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Sub(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose() => MirrorPaths.DeleteTree(Path);
}

/// <summary>
/// git for building fixtures, NOT the code under test: a hermetic environment (no system or user config, no inherited
/// GIT_* variables) so the machine's git config cannot change a fixture.
/// </summary>
internal static class FixtureGit
{
    private static readonly Lazy<string> EmptyConfig = new(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), "gitmirror-tests-empty.gitconfig");
        if (!File.Exists(path))
            File.WriteAllBytes(path, []);
        return path;
    });

    public static string EmptyConfigPath => EmptyConfig.Value;

    public static ProcessStartInfo StartInfo(string? dir, IEnumerable<string> args)
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
        psi.Environment["GIT_CONFIG_GLOBAL"] = EmptyConfigPath;
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (dir is not null)
        {
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(dir);
        }
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>Runs git; throws with its stderr on a non-zero exit.</summary>
    public static string Run(string? dir, params string[] args) => RunWithInput(dir, null, args);

    public static string RunWithInput(string? dir, string? input, params string[] args)
    {
        var (code, output, error) = TryRunWithInput(dir, input, args);
        if (code != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} exited {code}: {error}");
        return output;
    }

    public static (int Code, string Output, string Error) TryRun(string? dir, params string[] args) => TryRunWithInput(dir, null, args);

    public static (int Code, string Output, string Error) TryRunWithInput(string? dir, string? input, params string[] args)
    {
        using var process = Process.Start(StartInfo(dir, args))!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (input is not null)
        {
            // fast-import reads bytes; write UTF-8 without a BOM.
            var bytes = new UTF8Encoding(false).GetBytes(input);
            process.StandardInput.BaseStream.Write(bytes);
        }
        process.StandardInput.Close();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result, stderr.Result);
    }
}

/// <summary>
/// Builds a bare repository from a described DAG through <c>git fast-import</c>: fixed fixture identity, fixed timestamps
/// (so date order and every SHA are deterministic), empty trees, one commit per <see cref="Commit"/> named by its
/// subject.
/// </summary>
internal sealed class RepoBuilder
{
    /// <summary>2026-01-01T00:00:00Z: every commit time is this plus its offset in minutes.</summary>
    public const long Epoch = 1767225600;

    public const string Identity = "Test Author <author@example.invalid>";

    private readonly List<(string Name, string[] Parents, long Minutes)> commits = [];
    private readonly List<(string Ref, string Commit)> refs = [];
    private readonly List<(string Name, string Commit, string Message)> annotatedTags = [];

    public RepoBuilder Commit(string name, long minutes, params string[] parents)
    {
        commits.Add((name, parents, minutes));
        return this;
    }

    public RepoBuilder Branch(string name, string commit)
    {
        refs.Add(($"refs/heads/{name}", commit));
        return this;
    }

    /// <param name="message">An annotated tag's message; its name when null.</param>
    public RepoBuilder Tag(string name, string commit, bool annotated = false, string? message = null)
    {
        if (annotated)
            annotatedTags.Add((name, commit, message ?? name));
        else
            refs.Add(($"refs/tags/{name}", commit));
        return this;
    }

    /// <summary>Writes the repository (a new bare repository at <paramref name="path"/>, or an existing one, whose refs are
    /// then moved, force-push style) and returns commit name → SHA.</summary>
    public Dictionary<string, string> WriteTo(string path)
    {
        if (!Directory.Exists(path) || !File.Exists(Path.Combine(path, "HEAD")))
        {
            FixtureGit.Run(null, "init", "--quiet", "--bare", "--initial-branch=main", path);
            FixtureGit.Run(path, "config", "uploadpack.allowFilter", "true");
            FixtureGit.Run(path, "config", "http.receivepack", "false");
        }
        var marks = new Dictionary<string, int>();
        var stream = new StringBuilder();
        foreach (var (name, parents, minutes) in commits)
        {
            var mark = marks.Count + 1;
            marks[name] = mark;
            var when = $"{Epoch + minutes * 60} +0000";
            stream.Append("reset refs/fixtures/build\n");
            stream.Append("commit refs/fixtures/build\n");
            stream.Append($"mark :{mark}\n");
            stream.Append($"author {Identity} {when}\n");
            stream.Append($"committer {Identity} {when}\n");
            AppendData(stream, name);
            for (var i = 0; i < parents.Length; i++)
                stream.Append(i == 0 ? "from " : "merge ").Append(Ref(parents[i], marks)).Append('\n');
            stream.Append('\n');
        }
        foreach (var (name, commit, message) in annotatedTags)
        {
            stream.Append($"tag {name}\nfrom {Ref(commit, marks)}\ntagger {Identity} {Epoch} +0000\n");
            AppendData(stream, message);
        }
        foreach (var (reference, commit) in refs)
            stream.Append($"reset {reference}\nfrom {Ref(commit, marks)}\n\n");

        var marksFile = Path.Combine(path, "fixture-marks");
        FixtureGit.RunWithInput(path, stream.ToString(), "fast-import", "--quiet", "--force", $"--export-marks={marksFile}");
        FixtureGit.TryRun(path, "update-ref", "-d", "refs/fixtures/build");

        var byMark = File.ReadAllLines(marksFile).Select(l => l.Split(' ')).ToDictionary(p => int.Parse(p[0][1..]), p => p[1]);
        File.Delete(marksFile);
        return marks.ToDictionary(m => m.Key, m => byMark[m.Value]);

        string Ref(string commit, Dictionary<string, int> known)
            => known.TryGetValue(commit, out var m) ? $":{m}" : commit; // an earlier build's commit by SHA
    }

    private static void AppendData(StringBuilder stream, string text)
    {
        var bytes = Encoding.UTF8.GetByteCount(text);
        stream.Append($"data {bytes}\n{text}\n");
    }
}
