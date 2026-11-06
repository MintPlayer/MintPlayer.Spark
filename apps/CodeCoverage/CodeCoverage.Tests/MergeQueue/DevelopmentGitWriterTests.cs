using CodeCoverage.MergeQueue;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CodeCoverage.Tests.MergeQueue;

/// <summary>
/// Which git writer the app talks to (owner, 2026-11-06): in Development with no sidecar URL, the writer runs in-process and
/// clones into a local folder; a URL always wins, and outside Development only the sidecar exists.
/// </summary>
public class DevelopmentGitWriterTests
{
    [Theory]
    [InlineData("Development", null, true)]
    [InlineData("Development", "", true)]
    [InlineData("Development", "http://localhost:8080", false)]
    [InlineData("Production", null, false)]
    [InlineData("Production", "http://coverage-git-writer:8080", false)]
    [InlineData("Staging", null, false)]
    public void The_writer_runs_in_process_only_in_Development_without_a_sidecar_url(string environment, string? sidecarUrl, bool expected)
        => MergeQueueServiceCollectionExtensions.InProcessWriterApplies(new Env(environment), sidecarUrl).Should().Be(expected);

    [Fact]
    public void The_local_root_defaults_to_Repos_and_a_configured_one_wins()
    {
        new MergeQueueOptions { LocalGitWriterRoot = @"D:\work" }.LocalGitWriterRootOrDefault.Should().Be(@"D:\work");

        var root = new MergeQueueOptions().LocalGitWriterRootOrDefault;
        Path.GetFileName(root).Should().Be("Repos");
        if (OperatingSystem.IsWindows())
            root.Should().Be(Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "Repos"));
        else
            root.Should().Be(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Repos"));
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "CodeCoverage";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
