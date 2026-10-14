using System.Diagnostics;
using System.Reflection;
using System.Text;
using CodeCoverage.Git;
using CodeCoverage.GitMirror.Tests._Infrastructure;

namespace CodeCoverage.GitMirror.Tests;

/// <summary>The process every git command runs as (D25, D26), read off the <see cref="ProcessStartInfo"/> the runner builds.</summary>
public class GitRunnerTests
{
    private readonly GitRunner runner = GraphFixtures.Runner();

    [Fact]
    public void A_command_gets_the_hardened_flags_and_environment_and_no_inherited_git_variable()
    {
        const string inherited = "GIT_GITMIRROR_TESTS_INHERITED";
        Environment.SetEnvironmentVariable(inherited, "1");
        try
        {
            var psi = runner.CreateStartInfo(new GitInvocation { Arguments = ["fetch", "--prune", "origin"], GitDirectory = "/data/git/github/1.git" });
            var args = psi.ArgumentList.ToList();

            args.Take(2).Should().Equal(["-C", "/data/git/github/1.git"]);
            args.TakeLast(3).Should().Equal(["fetch", "--prune", "origin"]);
            foreach (var flag in new[] { "protocol.version=2", "protocol.file.allow=never", "protocol.ext.allow=never", "maintenance.auto=false", "gc.auto=0", "submodule.recurse=false", $"core.hooksPath={runner.HooksPath}", "credential.helper=" })
                args.Should().Contain(flag);

            psi.UseShellExecute.Should().BeFalse();
            psi.Environment["GIT_TERMINAL_PROMPT"].Should().Be("0");
            psi.Environment["GIT_ASKPASS"].Should().Be("");
            psi.Environment["GIT_CONFIG_NOSYSTEM"].Should().Be("1");
            psi.Environment.ContainsKey("GIT_NO_LAZY_FETCH").Should().BeFalse("only reads forbid lazy fetches");
            psi.Environment.ContainsKey("GIT_CONFIG_COUNT").Should().BeFalse();
            psi.Environment.ContainsKey(inherited).Should().BeFalse("inherited GIT_* variables are cleared");
            new ProcessStartInfo("git").Environment.ContainsKey(inherited).Should().BeTrue("control: a plain start info inherits it");

            var global = psi.Environment["GIT_CONFIG_GLOBAL"]!;
            if (OperatingSystem.IsWindows())
                new FileInfo(global).Length.Should().Be(0);
            else
                global.Should().Be("/dev/null");
            Directory.EnumerateFileSystemEntries(runner.HooksPath).Should().BeEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable(inherited, null);
        }
    }

    [Fact]
    public void A_read_forbids_lazy_fetches()
    {
        var psi = runner.CreateStartInfo(new GitInvocation { Arguments = ["log"], Read = true });

        psi.Environment["GIT_NO_LAZY_FETCH"].Should().Be("1");
    }

    [Fact]
    public void A_credential_travels_only_as_an_environment_extra_header()
    {
        var token = "ghs_" + Guid.NewGuid().ToString("N");
        var psi = runner.CreateStartInfo(new GitInvocation { Arguments = ["clone", "https://example.invalid/o/r.git", "x"], Credential = new GitCredential("x-access-token", token) });

        psi.Environment["GIT_CONFIG_COUNT"].Should().Be("1");
        psi.Environment["GIT_CONFIG_KEY_0"].Should().Be("http.extraHeader");
        (psi.Environment["GIT_CONFIG_VALUE_0"] == "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token}")))
            .Should().BeTrue("the header carries the credential");
        psi.ArgumentList.Any(a => a.Contains(token)).Should().BeFalse("the token is in no argument");
    }

    [Fact]
    public async Task A_command_past_its_timeout_is_killed_and_reported()
    {
        // A server that accepts the connection and never answers: ls-remote hangs until the runner kills it.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var clock = Stopwatch.StartNew();
            var insecure = new GitRunner(new GitMirrorOptions { AllowInsecureHttp = true }.ToRunnerOptions(), Microsoft.Extensions.Logging.Abstractions.NullLogger<GitRunner>.Instance);
            var result = await insecure.RunAsync(new GitInvocation { Arguments = ["ls-remote", $"http://127.0.0.1:{port}/r.git"], Timeout = TimeSpan.FromSeconds(1) }, CancellationToken.None);

            result.TimedOut.Should().BeTrue();
            result.Succeeded.Should().BeFalse();
            clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        }
        finally
        {
            listener.Stop();
        }
    }
}

/// <summary>
/// The sidecar holds no database client and no GitHub App code (D1, plan M1): the process that parses hostile
/// repositories could not reach RavenDB or mint a token even if the network allowed it.
/// </summary>
public class SidecarIsolationTests
{
    private static bool Forbidden(string name)
        => name.StartsWith("Raven", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("Sparrow", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("Octokit", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("GitHubJwt", StringComparison.OrdinalIgnoreCase)
           || name.Equals("MintPlayer.Spark", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("MintPlayer.Spark.", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("CodeCoverage.", StringComparison.OrdinalIgnoreCase) && !name.Equals("CodeCoverage.GitMirror", StringComparison.OrdinalIgnoreCase) && !name.Equals("CodeCoverage.Git", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void The_sidecars_compiled_references_hold_no_database_client_no_GitHub_App_and_no_Spark_runtime()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<Assembly>([typeof(GitMirrorHost).Assembly]);
        while (queue.TryDequeue(out var assembly))
        {
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (reference.Name is null || !seen.Add(reference.Name))
                    continue;
                try
                {
                    queue.Enqueue(Assembly.Load(reference));
                }
                catch (FileNotFoundException)
                {
                    // A facade the runtime does not ship: its name is recorded all the same.
                }
            }
        }

        seen.Should().Contain("MintPlayer.AspNetCore.Endpoints", "control: the walk does see package references");
        seen.Where(Forbidden).Should().BeEmpty();
    }
}
