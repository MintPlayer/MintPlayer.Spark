using System.Diagnostics;
using System.Text;
using CodeCoverage.Git;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeCoverage.Git.Tests._Infrastructure;

/// <summary>
/// A real repository built with the git CLI (memory: parsers need REAL fixtures): a work tree to commit in and a bare
/// clone the code under test runs against, as the git writer will. Fixture git runs hermetically (no system or user
/// config, no inherited GIT_* variables, fixed identities and dates) so the machine's config cannot change a SHA.
/// </summary>
internal sealed class TestRepo : IDisposable
{
    /// <summary>2026-01-01T00:00:00Z; each commit is one minute later than the previous one.</summary>
    private const long Epoch = 1767225600;

    private static readonly Lazy<string> EmptyConfig = new(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), "codecoverage-git-tests-empty.gitconfig");
        if (!File.Exists(path))
            File.WriteAllBytes(path, []);
        return path;
    });

    private int commits;

    public TestRepo()
    {
        Root = Path.Combine(Path.GetTempPath(), "codecoverage-git-tests", Guid.NewGuid().ToString("N")[..12]);
        Work = Path.Combine(Root, "work");
        Directory.CreateDirectory(Work);
        Git(null, "init", "--quiet", "--initial-branch=main", Work);
    }

    public string Root { get; }
    public string Work { get; }

    public static GitRunner Runner(bool checkReceivedObjects = false)
        => new(new GitRunnerOptions { CheckReceivedObjects = checkReceivedObjects }, NullLogger<GitRunner>.Instance);

    public TestRepo Write(string path, string content)
    {
        var full = Path.Combine(Work, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new UTF8Encoding(false).GetBytes(content));
        return this;
    }

    public TestRepo Delete(string path)
    {
        File.Delete(Path.Combine(Work, path));
        return this;
    }

    public string Commit(string message)
    {
        Git(Work, "add", "--all");
        var date = $"{Epoch + 60 * ++commits} +0000";
        Run(Work, null, new Dictionary<string, string> { ["GIT_AUTHOR_DATE"] = date, ["GIT_COMMITTER_DATE"] = date },
            "-c", "user.name=Fixture Author", "-c", "user.email=author@example.invalid", "commit", "--quiet", "--allow-empty", "-m", message);
        return Git(Work, "rev-parse", "HEAD").Trim();
    }

    /// <summary>Switches to <paramref name="branch"/>, creating it at <paramref name="from"/> when given.</summary>
    public TestRepo Checkout(string branch, string? from = null)
    {
        if (from is null)
            Git(Work, "checkout", "--quiet", branch);
        else
            Git(Work, "checkout", "--quiet", "-b", branch, from);
        return this;
    }

    /// <summary>A fresh bare clone holding every branch of the work tree.</summary>
    public string Bare()
    {
        var bare = Path.Combine(Root, $"bare-{Guid.NewGuid():N}.git"[..17]);
        Git(null, "clone", "--quiet", "--bare", Work, bare);
        return bare;
    }

    /// <summary>git in the work tree.</summary>
    public string InWork(params string[] args) => Git(Work, args);

    public static string Git(string? dir, params string[] args)
    {
        var (code, output, error) = TryGit(dir, args);
        return code == 0 ? output : throw new InvalidOperationException($"git {string.Join(' ', args)} exited {code}: {error}");
    }

    public static (int Code, string Output, string Error) TryGit(string? dir, params string[] args) => Run(dir, null, null, args);

    private static (int Code, string Output, string Error) Run(string? dir, string? input, IReadOnlyDictionary<string, string>? env, params string[] args)
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
        // Fixtures are written with LF and must stay byte-exact on Windows too.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.autocrlf=false");
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (input is not null)
            process.StandardInput.BaseStream.Write(new UTF8Encoding(false).GetBytes(input));
        process.StandardInput.Close();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result, stderr.Result);
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A virus scanner or indexer holding a pack file; the temp directory is cleaned up eventually.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Spark's <c>App_Data/modelHashes.json</c> layout (apps/DemoApp/DemoApp/App_Data/modelHashes.json): two-space
/// indent, LF, roll-ups first, then the per-file and per-entity maps sorted by name. The hashes are fixture values.</summary>
internal static class ModelHashes
{
    public const string Path = "apps/DemoApp/DemoApp/App_Data/modelHashes.json";

    public static string Render(string rollUp, IEnumerable<string> entities, IReadOnlyDictionary<string, string>? fileHashOverrides = null)
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
        {
            var hash = fileHashOverrides?.GetValueOrDefault(names[i]) ?? Hash("file", names[i]);
            builder.Append($"    \"{names[i]}.json\": \"{hash}\"{(i < names.Count - 1 ? "," : "")}\n");
        }
        builder.Append("  },\n");
        builder.Append("  \"entities\": {\n");
        for (var i = 0; i < names.Count; i++)
            builder.Append($"    \"{names[i]}\": \"{Hash("entity", names[i])}\"{(i < names.Count - 1 ? "," : "")}\n");
        builder.Append("  }\n");
        builder.Append("}\n");
        return builder.ToString();
    }

    /// <summary>A deterministic 64-hex fixture value.</summary>
    public static string Hash(string kind, string seed)
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(kind + ":" + seed)));
}
