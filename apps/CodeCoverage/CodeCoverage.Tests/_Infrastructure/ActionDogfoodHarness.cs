using CodeCoverage.Forge;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CodeCoverage.ApiTokens;
using CodeCoverage.Entities;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests._Infrastructure;

/// <summary>
/// The plumbing shared by every test that runs the <b>committed action bundle</b> against a
/// <b>live CodeCoverage server</b>: locating the repository root and the server assembly, starting
/// the server against the test's RavenDB, waiting for it to become healthy, and invoking
/// <c>dist/index.js</c> the way a runner does.
///
/// <para>
/// Extracted from <see cref="UploadActionDogfoodTests"/> when a second such test arrived (the
/// Windows-path case). The two differ only in what they put in the workspace and what they assert
/// about the resulting build; everything up to that point is identical, and duplicating it is how
/// the two would drift apart.
/// </para>
/// </summary>
public static class ActionDogfoodHarness
{
    /// <summary>
    /// Walks up from the test binaries to the repository root, identified by the solution file.
    /// Beats a pile of <c>..\..\..</c>: the number of levels changes with the target framework and
    /// the configuration, and the failure mode is a path that does not exist rather than a message.
    /// </summary>
    public static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MintPlayer.Spark.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    /// <summary>The committed action bundle, which is what a consumer actually executes.</summary>
    public static string ActionBundle(string repositoryRoot)
    {
        var bundle = Path.Combine(repositoryRoot, "apps", "CodeCoverage", "action", "dist", "index.js");
        Assert.True(File.Exists(bundle),
            $"The action bundle is missing at {bundle}. Run `npm run build` in apps/CodeCoverage/action.");
        return bundle;
    }

    /// <summary>
    /// The server's own build output, not the copy sitting beside the tests: a ProjectReference
    /// copies <c>CodeCoverage.dll</c> here but not its <c>runtimeconfig.json</c>, so the copy cannot
    /// be executed.
    /// </summary>
    public static string ServerAssembly(string repositoryRoot)
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
    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Seeds the repository and an API token the way minting one through the API does: the token is
    /// generated and hashed, and only the hash is stored. The value never leaves this process except
    /// as a request header.
    /// </summary>
    public static async Task<string> SeedRepositoryAndTokenAsync(
        IDocumentStore store, long repoId, string fullName, string ownerLogin, string name)
    {
        var token = ApiTokenService.GenerateTokenValue();
        using var session = store.OpenAsyncSession();

        await session.StoreAsync(new Repository
        {
            GitHubId = repoId,
            Name = name,
            FullName = fullName,
            OwnerLogin = ownerLogin,
            IsPrivate = false,
            DefaultBranch = "master",
        }, Repository.DocumentId(EForgeProvider.GitHub, repoId));

        await session.StoreAsync(new ApiToken
        {
            Scope = "Account",
            AccountLogin = ownerLogin,
            Description = "dogfood",
            CreatedByUserId = "test",
            CreatedAtUtc = DateTime.UtcNow,
        }, $"ApiTokens/{ApiTokenService.Hash(token)}");

        await session.SaveChangesAsync();
        return token;
    }

    /// <summary>
    /// Starts the real server process against the test's RavenDB and returns it together with a
    /// live view of its output, which is what turns a startup failure into a readable assertion.
    /// </summary>
    public static (Process Server, List<string> Output) StartServer(
        string repositoryRoot, string baseUrl, IDocumentStore store)
    {
        var start = new ProcessStartInfo("dotnet", $"\"{ServerAssembly(repositoryRoot)}\"")
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

        var server = Process.Start(start);
        Assert.NotNull(server);

        var output = new List<string>();
        server!.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.Add(e.Data); };
        server.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.Add(e.Data); };
        server.BeginOutputReadLine();
        server.BeginErrorReadLine();

        return (server, output);
    }

    /// <summary>
    /// Polls <c>/health</c> until the server answers, failing with its own output rather than a
    /// bare timeout -- a startup failure (a missing security.json, an unreachable database) is
    /// otherwise indistinguishable from a slow start.
    /// </summary>
    public static async Task WaitUntilHealthyAsync(Process server, string baseUrl, List<string> serverOutput)
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
    /// <paramref name="extraInputs"/> adds or overrides <c>INPUT_*</c> entries.
    /// </summary>
    public static async Task<(int ExitCode, string Log)> RunActionAsync(
        string bundle,
        string workspace,
        string outputFile,
        string baseUrl,
        string token,
        string repositoryFullName,
        string sha,
        long runId,
        IReadOnlyDictionary<string, string>? extraInputs = null)
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
            ["GITHUB_REPOSITORY"] = repositoryFullName,
            ["GITHUB_SHA"] = sha,
            ["GITHUB_RUN_ID"] = runId.ToString(),
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
        if (extraInputs is not null)
            foreach (var (key, value) in extraInputs) environment[key] = value;

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
    /// Makes <paramref name="workspace"/> a real git repository with <paramref name="paths"/>
    /// committed, so the action's <c>git ls-files -s</c> returns a populated v2 file list.
    /// Without this the upload carries no file list at all, which silently changes which branch of
    /// <c>PathNormalizer</c> resolves the report's paths.
    /// </summary>
    public static void InitGitRepositoryWith(string workspace, params string[] paths)
    {
        foreach (var relative in paths)
        {
            var full = Path.Combine(workspace, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if (!File.Exists(full)) File.WriteAllText(full, "// fixture\n");
        }

        Git(workspace, "init --initial-branch=master");
        Git(workspace, "config user.email dogfood@example.invalid");
        Git(workspace, "config user.name Dogfood");
        Git(workspace, "config commit.gpgsign false");
        Git(workspace, "add -A");
        Git(workspace, "commit -m fixture --no-verify");
    }

    /// <summary>
    /// Deletes a temp workspace without letting cleanup decide the test's verdict. git marks the
    /// blobs under <c>.git/objects</c> read-only, and <see cref="DirectoryInfo.Delete(bool)"/> then
    /// throws <see cref="UnauthorizedAccessException"/> — which is not an
    /// <see cref="IOException"/>, so a plain `catch (IOException)` lets it escape the finally block
    /// and replace the real assertion failure with a deletion failure.
    /// </summary>
    public static void DeleteWorkspaceBestEffort(DirectoryInfo workspace)
    {
        try
        {
            foreach (var file in workspace.GetFiles("*", SearchOption.AllDirectories))
                if (file.IsReadOnly) file.IsReadOnly = false;

            workspace.Delete(recursive: true);
        }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private static void Git(string workingDirectory, string arguments)
    {
        var start = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(start);
        Assert.NotNull(process);
        var stderr = process!.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {arguments} failed: {stderr}");
    }
}
