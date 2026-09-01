using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CodeCoverage.ApiTokens;
using CodeCoverage.Entities;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests;

/// <summary>
/// Runs the **committed action bundle** against a **running CodeCoverage server**, over a real
/// socket, through the whole upload → finish → status cycle.
///
/// <para>
/// This is the gate the standalone repository had (its <c>ci.yml</c> dogfooded with
/// <c>uses: ./action</c>) and the port to MintPlayer/github-actions lost, because an action cannot
/// be run against a server that lives in another repository. It is the reason the action's source
/// came back here — see <c>docs/coverage_action_home_PRD.md</c>.
/// </para>
///
/// <para>
/// Everything else that tests this pair tests one side of it: the action's own suite drives
/// <c>dist/index.js</c> against a stub HTTP server, and <c>UploadsControllerCapabilitiesTests</c>
/// calls the controller directly. Both pass while the two disagree about the thing between them —
/// a renamed form field, a multipart part the server parses differently, an auth header the
/// handler will not accept. Only this test fails then.
/// </para>
///
/// <para>
/// No service container and no secret: <see cref="CoverageRavenTest"/> already provides a live
/// RavenDB, so the server under test is the real <c>CodeCoverage.dll</c> pointed at it.
/// </para>
/// </summary>
public class UploadActionDogfoodTests : CoverageRavenTest
{
    private const long RepoId = 909090;
    private const string RepoName = "MintPlayer/dogfood";
    private const string Sha = "1111111111111111111111111111111111111111";
    private const long RunId = 777;

    /// <summary>
    /// Walks up from the test binaries to the repository root, identified by the solution file.
    /// Beats a pile of <c>..\..\..</c>: the number of levels changes with the target framework and
    /// the configuration, and the failure mode is a path that does not exist rather than a message.
    /// </summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MintPlayer.Spark.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    /// <summary>
    /// The server's own build output, not the copy sitting beside the tests: a ProjectReference
    /// copies <c>CodeCoverage.dll</c> here but not its <c>runtimeconfig.json</c>, so the copy cannot
    /// be executed.
    /// </summary>
    private static string ServerAssembly(string repositoryRoot)
    {
        var configuration =
#if DEBUG
            "Debug";
#else
            "Release";
#endif
        var directory = Path.Combine(repositoryRoot, "apps", "CodeCoverage", "CodeCoverage", "bin", configuration);
        var assembly = Directory.Exists(directory)
            ? Directory.GetFiles(directory, "CodeCoverage.dll", SearchOption.AllDirectories).FirstOrDefault()
            : null;

        Assert.True(assembly is not null,
            $"CodeCoverage.dll was not found under {directory}. Build the app before running this test.");
        return assembly!;
    }

    /// <summary>A port nothing is listening on. Racy in principle; the window is microseconds.</summary>
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task The_committed_bundle_uploads_to_a_live_server()
    {
        var repositoryRoot = RepositoryRoot();
        var bundle = Path.Combine(repositoryRoot, "apps", "CodeCoverage", "action", "dist", "index.js");
        Assert.True(File.Exists(bundle), $"The action bundle is missing at {bundle}. Run `npm run build` in apps/CodeCoverage/action.");

        using var store = GetDocumentStore();

        // The token is generated, hashed, and only the hash is stored -- exactly as minting one
        // through the API does. The value never leaves this process except as a request header.
        var token = ApiTokenService.GenerateTokenValue();
        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(new Repository
            {
                GitHubId = RepoId,
                Name = "dogfood",
                FullName = RepoName,
                OwnerLogin = "MintPlayer",
                IsPrivate = false,
                DefaultBranch = "master",
            }, Repository.DocumentId(RepoId));

            await session.StoreAsync(new ApiToken
            {
                Scope = "Account",
                AccountLogin = "MintPlayer",
                Description = "dogfood",
                CreatedByUserId = "test",
                CreatedAtUtc = DateTime.UtcNow,
            }, $"ApiTokens/{ApiTokenService.Hash(token)}");

            await session.SaveChangesAsync();
        }

        var port = FreePort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var serverAssembly = ServerAssembly(repositoryRoot);

        var start = new ProcessStartInfo("dotnet", $"\"{serverAssembly}\"")
        {
            // The app project directory, so App_Data/security.json resolves from the content root.
            WorkingDirectory = Path.Combine(repositoryRoot, "apps", "CodeCoverage", "CodeCoverage"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Production, not Development: the Development host spawns the Angular dev server through
        // UseAngularCliServer, which would start npm and fight for ports for no benefit here.
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["ASPNETCORE_URLS"] = baseUrl;
        start.Environment["Spark__RavenDb__Urls__0"] = store.Urls[0];
        start.Environment["Spark__RavenDb__Database"] = store.Database;
        // The OIDC audience the server validates. Irrelevant on the token path, but a mismatch here
        // is the sort of thing that only shows up much later.
        start.Environment["Coverage__BaseUrl"] = baseUrl;
        // Placeholders, not credentials. The app refuses to start without them -- GitHub is the only
        // sign-in provider it registers, so a missing client id means nobody could ever sign in, and
        // it says so loudly rather than starting half-configured. Ingestion never touches this path:
        // uploads authenticate with the ApiToken or GitHubOidc scheme, and a browser cookie is
        // deliberately not accepted there at all.
        start.Environment["GitHub__Production__ClientId"] = "dogfood-not-a-real-client";
        start.Environment["GitHub__Production__ClientSecret"] = "dogfood-not-a-real-secret";

        using var server = Process.Start(start);
        Assert.NotNull(server);
        var serverOutput = new List<string>();
        server!.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (serverOutput) serverOutput.Add(e.Data); };
        server.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (serverOutput) serverOutput.Add(e.Data); };
        server.BeginOutputReadLine();
        server.BeginErrorReadLine();

        try
        {
            await WaitUntilHealthy(server, baseUrl, serverOutput);

            var workspace = Directory.CreateTempSubdirectory("coverage-dogfood-");
            try
            {
                Directory.CreateDirectory(Path.Combine(workspace.FullName, "coverage"));
                await File.WriteAllTextAsync(
                    Path.Combine(workspace.FullName, "coverage", "lcov.info"),
                    "TN:\nSF:src/app.ts\nDA:1,1\nDA:2,0\nend_of_record\n");

                var outputFile = Path.Combine(workspace.FullName, "outputs.txt");
                await File.WriteAllTextAsync(outputFile, string.Empty);

                var (exitCode, log) = await RunAction(bundle, workspace.FullName, outputFile, baseUrl, token);

                Assert.True(exitCode == 0, $"The action failed (exit {exitCode}):\n{log}");
                // Proves the server accepted the multipart body and answered the shape the action
                // expects -- a 202 whose JSON carries buildId and sessionId.
                Assert.Contains("Upload accepted", log);

                var outputs = await File.ReadAllTextAsync(outputFile);
                Assert.Contains("build-id<<", outputs);
                // The capabilities probe reached a server that advertises the contract, rather than
                // falling back to the pre-capabilities baseline of 0.
                Assert.Matches(@"server-contract<<.*\r?\n1\r?\n", outputs);

                await AssertBuildParsed(store);
            }
            finally
            {
                try { workspace.Delete(recursive: true); } catch (IOException) { /* best effort */ }
            }
        }
        finally
        {
            if (!server.HasExited)
            {
                server.Kill(entireProcessTree: true);
                server.WaitForExit(10_000);
            }
        }
    }

    /// <summary>
    /// Polls <c>/health</c> until the server answers, failing with its own output rather than a
    /// bare timeout -- a startup failure (a missing security.json, an unreachable database) is
    /// otherwise indistinguishable from a slow start.
    /// </summary>
    private static async Task WaitUntilHealthy(Process server, string baseUrl, List<string> serverOutput)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow.AddSeconds(90);

        while (DateTime.UtcNow < deadline)
        {
            if (server.HasExited)
            {
                lock (serverOutput)
                    Assert.Fail($"The server exited with {server.ExitCode} before becoming healthy:\n{string.Join('\n', serverOutput.TakeLast(30))}");
            }

            try
            {
                var response = await client.GetAsync($"{baseUrl}/health");
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { /* not listening yet */ }
            catch (TaskCanceledException) { /* still starting */ }

            await Task.Delay(500);
        }

        lock (serverOutput)
            Assert.Fail($"The server was not healthy within 90s:\n{string.Join('\n', serverOutput.TakeLast(30))}");
    }

    /// <summary>
    /// Runs the bundle the way a runner does: inputs and workflow context through the environment.
    /// </summary>
    private static async Task<(int ExitCode, string Log)> RunAction(
        string bundle, string workspace, string outputFile, string baseUrl, string token)
    {
        var start = new ProcessStartInfo("node", $"\"{bundle}\"")
        {
            WorkingDirectory = workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        var environment = new Dictionary<string, string>
        {
            ["GITHUB_WORKSPACE"] = workspace,
            ["GITHUB_REPOSITORY"] = RepoName,
            ["GITHUB_SHA"] = Sha,
            ["GITHUB_RUN_ID"] = RunId.ToString(),
            ["GITHUB_RUN_ATTEMPT"] = "1",
            ["GITHUB_WORKFLOW"] = "dogfood",
            ["GITHUB_JOB"] = "dogfood",
            ["GITHUB_EVENT_NAME"] = "push",
            ["GITHUB_REF_NAME"] = "master",
            ["GITHUB_OUTPUT"] = outputFile,
            // Emptied rather than left inherited: on a real runner these carry the surrounding
            // workflow's context, and ACTIONS_ID_TOKEN_REQUEST_URL in particular would send the
            // action down the OIDC path against a server that is not GitHub.
            ["GITHUB_EVENT_PATH"] = string.Empty,
            ["GITHUB_HEAD_REF"] = string.Empty,
            ["ACTIONS_ID_TOKEN_REQUEST_URL"] = string.Empty,
            ["INPUT_URL"] = baseUrl,
            ["INPUT_TOKEN"] = token,
            ["INPUT_FINISH"] = "true",
            ["INPUT_WAIT-FOR-FINALIZE"] = "true",
            ["INPUT_WAIT-TIMEOUT"] = "120",
            ["INPUT_WAIT-POLL-INTERVAL"] = "2",
            ["INPUT_FAIL-CI-IF-ERROR"] = "true",
        };
        foreach (var (key, value) in environment) start.Environment[key] = value;

        using var action = Process.Start(start);
        Assert.NotNull(action);

        var stdout = await action!.StandardOutput.ReadToEndAsync();
        var stderr = await action.StandardError.ReadToEndAsync();
        await action.WaitForExitAsync();

        // ::debug lines are the bulk of the output and say nothing about the contract.
        var log = string.Join('\n', (stdout + stderr)
            .Split('\n')
            .Where(line => !line.StartsWith("::debug", StringComparison.Ordinal)));

        return (action.ExitCode, log);
    }

    /// <summary>
    /// The server-side truth. `wait-for-finalize` already blocked until the build left InFlight, so
    /// this is a short poll for the session's parse result rather than a wait on the pipeline.
    /// </summary>
    private static async Task AssertBuildParsed(IDocumentStore store)
    {
        var buildId = Build.DocumentId(RepoId, Sha, RunId, 1);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        Build? build = null;

        while (DateTime.UtcNow < deadline)
        {
            using var session = store.OpenAsyncSession();
            build = await session.LoadAsync<Build>(buildId);
            if (build?.Coverage is not null) break;
            await Task.Delay(500);
        }

        Assert.NotNull(build);
        Assert.NotEmpty(build!.Sessions);
        Assert.All(build.Sessions, s => Assert.Equal("Parsed", s.ParseStatus));
        Assert.NotNull(build.Coverage);
        // The report declared two coverable lines, one of them covered. Asserting the numbers
        // rather than mere presence is what catches a parser that silently drops a report whose
        // paths it cannot match.
        Assert.Equal(2, build.Coverage!.LinesCoverable);
        Assert.Equal(1, build.Coverage.LinesCovered);
    }
}
