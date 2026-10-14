using CodeCoverage.Git.Tests._Infrastructure;

namespace CodeCoverage.Git.Tests;

/// <summary>What the shared runner adds for the merge queue's writer (docs/coverage_merge_queue_PRD.md §4.3, T8): read
/// off the start info it builds, plus real runs for stdin, the throwaway index and the identities.</summary>
public class GitRunnerHardeningTests
{
    [Fact]
    public void Every_command_reads_attributes_from_the_empty_tree_and_never_starts_an_fsmonitor()
    {
        var args = TestRepo.Runner().CreateStartInfo(new GitInvocation { Arguments = ["merge-tree"] }).ArgumentList.ToList();

        args.Should().Contain($"attr.tree={GitRunner.EmptyTree}");
        args.Should().Contain("core.fsmonitor=false");
        args.Should().Contain("protocol.allow=never");
        args.Should().Contain("protocol.https.allow=always");
        args.Should().NotContain("transfer.fsckObjects=true");
    }

    [Fact]
    public void The_writer_checks_every_received_object()
    {
        var args = TestRepo.Runner(checkReceivedObjects: true).CreateStartInfo(new GitInvocation { Arguments = ["fetch"] }).ArgumentList.ToList();

        args.Should().Contain("transfer.fsckObjects=true");
    }

    [Theory]
    [InlineData("core.hooksPath")]
    [InlineData("attr.tree")]
    [InlineData("protocol.file.allow")]
    [InlineData("credential.helper")]
    [InlineData("not a key")]
    public void A_per_command_config_cannot_override_the_hardening(string key)
    {
        var invocation = new GitInvocation { Arguments = ["merge-tree"], Config = [new(key, "x")] };

        FluentActionsFor.Act(() => TestRepo.Runner().CreateStartInfo(invocation)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_per_command_config_goes_after_the_hardened_flags()
    {
        var args = TestRepo.Runner().CreateStartInfo(new GitInvocation { Arguments = ["merge-tree"], Config = [new("merge.conflictStyle", "diff3")] }).ArgumentList.ToList();

        args.IndexOf("merge.conflictStyle=diff3").Should().BeGreaterThan(args.IndexOf("credential.helper="));
        args.Last().Should().Be("merge-tree");
    }

    [Theory]
    [InlineData("Evil <x@y>", "a@example.invalid")]
    [InlineData("Line\nbreak", "a@example.invalid")]
    [InlineData("Name", "a b@example.invalid")]
    [InlineData("Name", "<a@example.invalid>")]
    public void An_identity_git_would_misparse_is_refused(string name, string email)
        => FluentActionsFor.Act(() => new GitIdentity(name, email)).Should().Throw<ArgumentException>();

    [Fact]
    public async Task Stdin_the_index_file_and_the_identities_reach_git()
    {
        using var repo = new TestRepo();
        var bare = repo.Bare();
        var runner = TestRepo.Runner();

        // stdin: the blob of "hello\n" has a well-known id.
        var blob = await runner.RunAsync(new GitInvocation { Arguments = ["hash-object", "-w", "--stdin"], GitDirectory = bare, StandardInput = "hello\n" }, CancellationToken.None);
        blob.StandardOutput.Trim().Should().Be("ce013625030ba8dba906f756967f9e9ca394464a");

        // A throwaway index in a bare repository: the tree holds the blob, and the repository has no index of its own.
        var index = Path.Combine(repo.Root, "throwaway.index");
        await runner.RunAsync(new GitInvocation { Arguments = ["update-index", "--add", "-z", "--index-info"], GitDirectory = bare, IndexFile = index, StandardInput = "100644 ce013625030ba8dba906f756967f9e9ca394464a\thello.txt\0" }, CancellationToken.None);
        var tree = (await runner.RunAsync(new GitInvocation { Arguments = ["write-tree"], GitDirectory = bare, IndexFile = index }, CancellationToken.None)).StandardOutput.Trim();
        TestRepo.Git(bare, "ls-tree", "--name-only", tree).Trim().Should().Be("hello.txt");
        File.Exists(Path.Combine(bare, "index")).Should().BeFalse();

        var when = new DateTimeOffset(2026, 10, 14, 8, 30, 0, TimeSpan.FromHours(-5));
        var commit = await runner.RunAsync(new GitInvocation
        {
            Arguments = ["commit-tree", tree, "-F", "-"],
            GitDirectory = bare,
            StandardInput = "subject\n",
            Author = new GitIdentity("An Author", "author@example.invalid", when),
            Committer = new GitIdentity("A Bot", "bot@example.invalid", when),
        }, CancellationToken.None);
        commit.Succeeded.Should().BeTrue(commit.StandardError);
        TestRepo.Git(bare, "log", "-1", "--format=%an|%ae|%cn|%ce|%ai|%s", commit.StandardOutput.Trim()).Trim()
            .Should().Be("An Author|author@example.invalid|A Bot|bot@example.invalid|2026-10-14 08:30:00 -0500|subject");
    }
}

internal static class FluentActionsFor
{
    public static Action Act(Action action) => action;

    public static Action Act<T>(Func<T> func) => () => func();
}
