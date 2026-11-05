using CodeCoverage.Versioning.Build;
using CodeCoverage.Versioning.Versions;

namespace CodeCoverage.Versioning.Tests.Build;

/// <summary>PRD D8: the SDK a checkout builds with, read with the checkout's own <c>global.json</c>.</summary>
public class DotnetSdkTests : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("dotnet-sdk-");

    public void Dispose() => root.Delete(recursive: true);

    [Fact]
    public async Task The_sdk_that_runs_the_tests_answers_with_its_version()
    {
        var version = await DotnetSdk.VersionAsync(root.FullName);

        version.Should().NotBeNull();
        SemVer.TryParse(version!, out _).Should().BeTrue();
        version.Should().StartWith(Environment.Version.Major + ".", "the test host runs on the SDK's own runtime major");
    }

    [Fact]
    public async Task A_global_json_pinning_a_missing_sdk_has_no_version()
    {
        File.WriteAllText(Path.Combine(root.FullName, "global.json"), """{ "sdk": { "version": "1.0.999", "rollForward": "disable" } }""");

        (await DotnetSdk.VersionAsync(root.FullName)).Should().BeNull();
    }

    [Fact]
    public async Task A_cancelled_read_has_no_version()
        => (await DotnetSdk.VersionAsync(root.FullName, new CancellationToken(canceled: true))).Should().BeNull();
}
