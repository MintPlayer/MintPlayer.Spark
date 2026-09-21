using CodeCoverage.Forge;
using CodeCoverage.Entities;
using CodeCoverage.Tests._Infrastructure;
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
/// RavenDB, so the server under test is the real <c>CodeCoverage.dll</c> pointed at it. The process
/// plumbing lives in <see cref="ActionDogfoodHarness"/>, shared with
/// <see cref="UploadActionWindowsPathsTests"/>.
/// </para>
/// </summary>
public class UploadActionDogfoodTests : CoverageRavenTest
{
    private const long RepoId = 909090;
    private const string RepoName = "MintPlayer/dogfood";
    private const string Sha = "1111111111111111111111111111111111111111";
    private const long RunId = 777;

    [Fact]
    public async Task The_committed_bundle_uploads_to_a_live_server()
    {
        var repositoryRoot = ActionDogfoodHarness.RepositoryRoot();
        var bundle = ActionDogfoodHarness.ActionBundle(repositoryRoot);

        using var store = GetDocumentStore();
        var token = await ActionDogfoodHarness.SeedRepositoryAndTokenAsync(
            store, RepoId, RepoName, "MintPlayer", "dogfood");

        var port = ActionDogfoodHarness.FreePort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var (server, serverOutput) = ActionDogfoodHarness.StartServer(repositoryRoot, baseUrl, store);

        try
        {
            await ActionDogfoodHarness.WaitUntilHealthyAsync(server, baseUrl, serverOutput);

            var workspace = Directory.CreateTempSubdirectory("coverage-dogfood-");
            try
            {
                Directory.CreateDirectory(Path.Combine(workspace.FullName, "coverage"));
                await File.WriteAllTextAsync(
                    Path.Combine(workspace.FullName, "coverage", "lcov.info"),
                    "TN:\nSF:src/app.ts\nDA:1,1\nDA:2,0\nend_of_record\n");

                var outputFile = Path.Combine(workspace.FullName, "outputs.txt");
                await File.WriteAllTextAsync(outputFile, string.Empty);

                var (exitCode, log) = await ActionDogfoodHarness.RunActionAsync(
                    bundle, workspace.FullName, outputFile, baseUrl, token, RepoName, Sha, RunId);

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
                ActionDogfoodHarness.DeleteWorkspaceBestEffort(workspace);
            }
        }
        finally
        {
            if (!server.HasExited)
            {
                server.Kill(entireProcessTree: true);
                server.WaitForExit(10_000);
            }
            server.Dispose();
        }
    }

    /// <summary>
    /// The server-side truth. `wait-for-finalize` already blocked until the build left InFlight, so
    /// this is a short poll for the session's parse result rather than a wait on the pipeline.
    /// </summary>
    private static async Task AssertBuildParsed(IDocumentStore store)
    {
        var buildId = Build.DocumentId(EForgeProvider.GitHub, RepoId, Sha, RunId, 1);
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
