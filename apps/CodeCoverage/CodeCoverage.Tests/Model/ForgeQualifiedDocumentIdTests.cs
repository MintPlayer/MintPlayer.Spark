using CodeCoverage.Entities;
using CodeCoverage.Forge;
using Xunit;

namespace CodeCoverage.Tests.Model;

/// <summary>
/// A2 — the collision the provider segment exists to prevent, plus the id parse that the
/// authorization path depends on.
/// </summary>
/// <remarks>
/// RavenDB document ids are immutable, so a scheme that admits a collision is not a bug that can
/// be patched later: it is 224,000 documents re-keyed a second time. These tests are cheap
/// insurance against that.
/// </remarks>
public class ForgeQualifiedDocumentIdTests
{
    /// <summary>
    /// ⚠️ The whole point. GitHub repository 1234 and GitLab project 1234 are different
    /// repositories, and a numeric forge id is unique only <em>within</em> its forge.
    /// </summary>
    [Fact]
    public void The_same_numeric_id_on_two_forges_is_two_documents()
    {
        var github = Repository.DocumentId(EForgeProvider.GitHub, 1234);
        var gitlab = Repository.DocumentId(EForgeProvider.GitLab, 1234);

        github.Should().NotBe(gitlab);
        github.Should().Be("Repositories/github/1234");
        gitlab.Should().Be("Repositories/gitlab/1234");
    }

    [Theory]
    [InlineData(EForgeProvider.GitHub, "github")]
    [InlineData(EForgeProvider.GitLab, "gitlab")]
    [InlineData(EForgeProvider.Bitbucket, "bitbucket")]
    public void Every_collection_carries_the_same_provider_spelling(EForgeProvider provider, string segment)
    {
        Repository.DocumentId(provider, 1).Should().Be($"Repositories/{segment}/1");
        Account.DocumentId(provider, 2).Should().Be($"Accounts/{segment}/2");
        Commit.DocumentId(provider, 3, "abc").Should().Be($"Commits/{segment}/3/abc");
        PullRequestFeedback.DocumentId(provider, 4, 7).Should().Be($"PullRequestFeedbacks/{segment}/4/7");
    }

    /// <summary>
    /// Nine id shapes inherit the segment from <see cref="Commit.DocumentId"/>; this pins the two
    /// that compose on top of it, so a change there cannot silently desynchronise them.
    /// </summary>
    [Fact]
    public void Nested_ids_inherit_the_segment_from_the_commit_id()
    {
        var commit = Commit.DocumentId(EForgeProvider.GitLab, 42, "deadbeef");
        var build = Build.DocumentId(EForgeProvider.GitLab, 42, "deadbeef", 99, 1);

        build.Should().StartWith(commit).And.Be("Commits/gitlab/42/deadbeef/builds/99-1");
        BuildTreeSummary.DocumentId(build).Should().Be(build + "/tree");
        FileCoverage.DocumentId(build, "src/a.cs").Should().StartWith(build + "/files/");
    }

    /// <summary>
    /// D6f — the fork shape is reserved but unused. Nothing writes it today; it exists because
    /// deciding it later would cost the whole re-key a second time.
    /// </summary>
    [Fact]
    public void The_fork_pull_request_shape_is_reserved()
    {
        var head = Commit.DocumentId(EForgeProvider.GitHub, 42, "abc", pullRequestNumber: 7);

        head.Should().Be("Commits/github/42/pr/7/abc");
        head.Should().NotBe(Commit.DocumentId(EForgeProvider.GitHub, 42, "abc"),
            "a fork's head must not collide with the same sha pushed to the upstream repository");
    }

    /// <summary>
    /// ⚠️ The parse that <c>BuildActions.IsAllowedAsync</c> depends on. It used to require
    /// <c>parts[1]</c> to be a <c>long</c>; with the forge segment that reads "github", the parse
    /// fails, and authorization denies <b>every build in the system</b> — silently, because it
    /// fails closed and the grid merely goes blank.
    /// </summary>
    [Theory]
    [InlineData("Commits/github/402741072/abc", EForgeProvider.GitHub, 402741072L)]
    [InlineData("Commits/gitlab/99/abc", EForgeProvider.GitLab, 99L)]
    [InlineData("Commits/github/42/pr/7/abc", EForgeProvider.GitHub, 42L)]
    [InlineData("Commits/github/42/abc/builds/99-1", EForgeProvider.GitHub, 42L)]
    [InlineData("Commits/github/42/abc/builds/99-1/files/deadbeef", EForgeProvider.GitHub, 42L)]
    public void A_commit_id_and_anything_under_it_resolves_to_its_repository(
        string id, EForgeProvider expectedProvider, long expectedRepositoryId)
    {
        Commit.TryParseRepository(id, out var provider, out var repositoryId).Should().BeTrue();
        provider.Should().Be(expectedProvider);
        repositoryId.Should().Be(expectedRepositoryId);
    }

    /// <summary>
    /// The legacy shape still resolves, so a half-finished migration renders rather than blanks.
    /// ⚠️ M6g deletes this branch once the re-key is confirmed complete in production.
    /// </summary>
    [Fact]
    public void A_legacy_unqualified_commit_id_still_resolves_to_GitHub()
    {
        Commit.TryParseRepository("Commits/402741072/abc", out var provider, out var repositoryId)
            .Should().BeTrue();
        provider.Should().Be(EForgeProvider.GitHub);
        repositoryId.Should().Be(402741072L);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Commits")]
    [InlineData("Commits/github")]
    [InlineData("Repositories/github/42")]
    [InlineData("Commits/notaforge/abc")]
    public void Anything_that_is_not_a_commit_id_is_refused(string? id)
        => Commit.TryParseRepository(id, out _, out _).Should().BeFalse();
}
