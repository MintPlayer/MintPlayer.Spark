using CodeCoverage.Forge;
using CodeCoverage.Entities;
using CodeCoverage.Tests._Infrastructure;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests;

/// <summary>
/// The regression gate for issue #415: a coverage report whose paths are <b>absolute and in the
/// host's native form</b> — what every collector writes on a Windows runner — must still resolve to
/// repository-relative paths and count towards the build's coverage.
///
/// <para>
/// This is deliberately end-to-end (committed bundle → real server → RavenDB) rather than a
/// <c>PathNormalizer</c> unit test. The unit level already asserted this and passed, while a real
/// upload from a Windows runner produced an empty report — so the gap was never in the normaliser's
/// logic but somewhere in the pipeline around it: what the action sends, what survives the form,
/// what the file list contains. Only a test that spans the whole path can see that.
/// </para>
///
/// <para>
/// The workspace is a real git repository, which matters: without one, <c>git ls-files</c> returns
/// nothing, the upload carries an empty file list, and the normaliser silently takes a different
/// branch than it does in production.
/// </para>
/// </summary>
public class UploadActionWindowsPathsTests : CoverageRavenTest
{
    private const long RepoId = 909091;
    private const string RepoName = "MintPlayer/dogfood-windows";
    private const string Sha = "2222222222222222222222222222222222222222";
    private const long RunId = 778;

    /// <summary>
    /// Cobertura as <c>Microsoft.Testing.Extensions.CodeCoverage</c> writes it on a Windows runner:
    /// an absolute <c>&lt;source&gt;</c> and absolute, backslash-separated <c>filename</c>
    /// attributes. The shape is taken from the report in issue #415.
    /// </summary>
    private static string WindowsCobertura(string workspaceRoot)
    {
        var native = workspaceRoot.Replace('/', '\\');
        return $"""
        <?xml version="1.0" encoding="utf-8"?>
        <coverage line-rate="0.5" branch-rate="0" version="1.9" timestamp="0" lines-covered="1" lines-valid="2" branches-covered="0" branches-valid="0">
          <sources>
            <source>{native}\</source>
          </sources>
          <packages>
            <package name="Fixture" line-rate="0.5" branch-rate="0" complexity="0">
              <classes>
                <class name="Fixture.Mesh" filename="{native}\ThreeDee\MintPlayer.ThreeDee\Geometry\Mesh.cs" line-rate="0.5" branch-rate="0" complexity="0">
                  <methods />
                  <lines>
                    <line number="1" hits="1" branch="false" />
                    <line number="2" hits="0" branch="false" />
                  </lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>
        """;
    }

    [Fact]
    public async Task A_report_with_absolute_windows_paths_resolves_and_counts()
    {
        var repositoryRoot = ActionDogfoodHarness.RepositoryRoot();
        var bundle = ActionDogfoodHarness.ActionBundle(repositoryRoot);

        using var store = GetDocumentStore();
        var token = await ActionDogfoodHarness.SeedRepositoryAndTokenAsync(
            store, RepoId, RepoName, "MintPlayer", "dogfood-windows");

        var port = ActionDogfoodHarness.FreePort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var (server, serverOutput) = ActionDogfoodHarness.StartServer(repositoryRoot, baseUrl, store);

        try
        {
            await ActionDogfoodHarness.WaitUntilHealthyAsync(server, baseUrl, serverOutput);

            var workspace = Directory.CreateTempSubdirectory("coverage-windows-");
            try
            {
                // The file the report measures has to exist in `git ls-files`, or the normaliser is
                // being asked to resolve a path against an empty tree — a different code path.
                ActionDogfoodHarness.InitGitRepositoryWith(
                    workspace.FullName, "ThreeDee/MintPlayer.ThreeDee/Geometry/Mesh.cs");

                Directory.CreateDirectory(Path.Combine(workspace.FullName, "coverage"));
                await File.WriteAllTextAsync(
                    Path.Combine(workspace.FullName, "coverage", "report.cobertura.xml"),
                    WindowsCobertura(workspace.FullName));

                var outputFile = Path.Combine(workspace.FullName, "outputs.txt");
                await File.WriteAllTextAsync(outputFile, string.Empty);

                var (exitCode, log) = await ActionDogfoodHarness.RunActionAsync(
                    bundle, workspace.FullName, outputFile, baseUrl, token, RepoName, Sha, RunId,
                    new Dictionary<string, string>
                    {
                        ["INPUT_FILES"] = "coverage/*.cobertura.xml",
                        ["INPUT_DISABLE-SEARCH"] = "true",
                    });

                Assert.True(exitCode == 0, $"The action failed (exit {exitCode}):\n{log}");
                Assert.Contains("Upload accepted", log);

                await AssertFileMatchedAndCounted(store);
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
    /// Asserts the numbers, not merely that a build exists. An unmatched file is retained with
    /// <c>Matched = false</c> and then excluded from the build's summary, so "a build was created"
    /// and "the report was understood" are different claims — and the bug in #415 is precisely a
    /// build that exists while its coverage is empty.
    /// </summary>
    private static async Task AssertFileMatchedAndCounted(IDocumentStore store)
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

        build.Should().NotBeNull("the upload was accepted, so a build must exist");
        build!.Sessions.Should().NotBeEmpty();
        build.Sessions.Select(s => s.ParseStatus).Distinct().Should().Equal("Parsed");
        build.Coverage.Should().NotBeNull();

        // The report declared two coverable lines, one covered. Zero here is the #415 symptom:
        // parsed cleanly, matched nothing, summarised to nothing.
        build.Coverage!.LinesCoverable.Should().Be(2);
        build.Coverage.LinesCovered.Should().Be(1);
        build.Coverage.FilesCount.Should().Be(1);

        using var check = store.OpenAsyncSession();
        // Scoped to the build's own files by id prefix. Querying on BuildId alone also returns the
        // commit-assembly copy (`…/assembly/files/…`), which carries the same BuildId and would
        // read as a duplicate.
        var files = (await check.Query<FileCoverage>()
                .Where(f => f.BuildId == buildId)
                .ToListAsync())
            .Where(f => f.Id!.StartsWith($"{buildId}/files/", StringComparison.Ordinal))
            .ToList();

        files.Should().HaveCount(1);
        var file = files[0];
        file.Matched.Should().BeTrue($"the file was stored unmatched, as '{file.Path}'");
        file.Path.Should().Be("ThreeDee/MintPlayer.ThreeDee/Geometry/Mesh.cs");
    }
}
