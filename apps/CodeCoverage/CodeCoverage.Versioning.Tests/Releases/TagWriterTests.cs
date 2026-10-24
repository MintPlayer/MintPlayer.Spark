using CodeCoverage.Versioning.Git;
using CodeCoverage.Versioning.Releases;
using CodeCoverage.Versioning.Versions;
using static CodeCoverage.Versioning.Tests.Releases.ReleasePlannerTests;

namespace CodeCoverage.Versioning.Tests.Releases;

/// <summary>M5: the tag writer against a local bare remote. A rerun is a no-op, a version never moves, and a commit
/// outside the allowed branches is never tagged.</summary>
public class TagWriterTests
{
    private const string Hash = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly Tagger Bot = new("codecoverage[bot]", "bot@example.invalid");

    private sealed class Repository : IDisposable
    {
        private readonly DirectoryInfo work = Directory.CreateTempSubdirectory("versioning-tags-");

        public Repository()
        {
            Directory.CreateDirectory(Remote);
            Run(Remote, "init", "-q", "--bare", "-b", "main");
            Directory.CreateDirectory(Checkout);
            Run(Checkout, "init", "-q", "-b", "main");
            Run(Checkout, "remote", "add", "origin", Remote);
            First = Commit("first");
            Second = Commit("second");
            Run(Checkout, "push", "-q", "origin", "main");
            Run(Checkout, "fetch", "-q", "origin");
            Run(Checkout, "switch", "-q", "-c", "side");
            Side = Commit("side");
        }

        public string Remote => Path.Combine(work.FullName, "remote.git");
        public string Checkout => Path.Combine(work.FullName, "checkout");
        public string First { get; }
        public string Second { get; }

        /// <summary>A commit on a local branch only: not on the default branch.</summary>
        public string Side { get; }

        public GitCli Git => new(Path.Combine(Checkout, ".git"));

        public TagWriter Writer() => new(new CliTagGit(Git, token: null), "origin", ["refs/remotes/origin/main"], Bot);

        public async Task<string?> RemoteTagCommitAsync(string name)
        {
            var result = await new GitCli(Remote).RunAsync(["rev-parse", "--verify", "--quiet", $"refs/tags/{name}^{{commit}}"]);
            return result.ExitCode == 0 ? result.Text.Trim() : null;
        }

        private string Commit(string message)
        {
            File.WriteAllText(Path.Combine(Checkout, message + ".txt"), message);
            Run(Checkout, "add", ".");
            Run(Checkout, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", message);
            return Git.RunAsync(["rev-parse", "HEAD"]).GetAwaiter().GetResult().EnsureSuccess("rev-parse").Text.Trim();
        }

        public void Dispose()
        {
            foreach (var file in work.EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal;
            work.Delete(recursive: true);
        }
    }

    private static TagToWrite Tag(string id, string version, string commit)
        => new(VersionTag.For(id, PackageVersion.Parse(version)), commit, new TagAnnotation(Hash, null));

    [Fact]
    public async Task Pushes_annotated_tags_and_a_rerun_writes_nothing()
    {
        using var repository = new Repository();
        TagToWrite[] tags = [Tag("Core", "10.0.0", repository.First), Tag("Mapper", "10.0.0", repository.Second)];

        var first = await repository.Writer().WriteAsync(tags);
        first.Select(r => r.Outcome).Should().BeEquivalentTo([TagOutcome.Created, TagOutcome.Created]);
        (await repository.RemoteTagCommitAsync("Core/v10.0.0")).Should().Be(repository.First);
        (await repository.RemoteTagCommitAsync("Mapper/v10.0.0")).Should().Be(repository.Second);

        // The pushed tag is annotated, and its annotation is the one TagHistory reads back (D6).
        var history = await TagHistory.ReadAsync(new GitCli(repository.Remote));
        history.Of("Core").Should().ContainSingle().Which.Annotation!.ApiHash.Should().Be(Hash);

        var rerun = await repository.Writer().WriteAsync(tags);
        rerun.Select(r => r.Outcome).Should().BeEquivalentTo([TagOutcome.AlreadyPresent, TagOutcome.AlreadyPresent]);
    }

    [Fact]
    public async Task A_commit_outside_the_allowed_branches_is_refused()
    {
        using var repository = new Repository();

        var result = await repository.Writer().WriteAsync([Tag("Core", "10.0.0", repository.Side)]);

        result.Should().ContainSingle().Which.Outcome.Should().Be(TagOutcome.Unreachable);
        (await repository.RemoteTagCommitAsync("Core/v10.0.0")).Should().BeNull();
    }

    [Fact]
    public async Task A_version_on_another_commit_is_a_conflict_and_never_moves()
    {
        using var repository = new Repository();
        await repository.Writer().WriteAsync([Tag("Core", "10.0.0", repository.First)]);

        var result = await repository.Writer().WriteAsync([Tag("Core", "10.0.0", repository.Second), Tag("Mapper", "10.0.0", repository.Second)]);

        result.Select(r => r.Outcome).Should().BeEquivalentTo([TagOutcome.Conflict, TagOutcome.Created]);
        (await repository.RemoteTagCommitAsync("Core/v10.0.0")).Should().Be(repository.First);
    }

    [Fact]
    public async Task A_tag_another_run_pushed_after_the_check_ends_as_already_present()
    {
        using var repository = new Repository();
        var tag = Tag("Core", "10.0.0", repository.First);
        // The other run's tag reaches the remote between this run's ls-remote and its push.
        var racing = new RacingGit(new CliTagGit(repository.Git, token: null), async () =>
        {
            var other = Directory.CreateTempSubdirectory("versioning-other-");
            try
            {
                Run(other.FullName, "clone", "-q", repository.Remote, ".");
                Run(other.FullName, "-c", "user.name=t", "-c", "user.email=t@t", "tag", "-a", "-m", "other", "Core/v10.0.0", repository.First);
                Run(other.FullName, "push", "-q", "origin", "refs/tags/Core/v10.0.0");
            }
            finally
            {
                foreach (var file in other.EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal;
                other.Delete(recursive: true);
            }
        });

        var result = await new TagWriter(racing, "origin", ["refs/remotes/origin/main"], Bot).WriteAsync([tag]);

        result.Should().ContainSingle().Which.Outcome.Should().Be(TagOutcome.AlreadyPresent);
    }

    /// <summary>Runs <paramref name="beforePush"/> once, just before the first push.</summary>
    private sealed class RacingGit(ITagGit inner, Func<Task> beforePush) : ITagGit
    {
        private bool raced;

        public async Task<GitCli.Result> RunAsync(IReadOnlyList<string> arguments, string? input, bool authenticated, Tagger? tagger, CancellationToken cancellationToken)
        {
            if (arguments[0] == "push" && !raced)
            {
                raced = true;
                await beforePush();
            }
            return await inner.RunAsync(arguments, input, authenticated, tagger, cancellationToken);
        }
    }
}
