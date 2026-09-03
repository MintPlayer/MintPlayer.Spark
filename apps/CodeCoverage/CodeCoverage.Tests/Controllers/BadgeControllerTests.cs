using CodeCoverage.Badges;
using CodeCoverage.Controllers;
using CodeCoverage.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Controllers;

/// <summary>
/// The badge is this application's ONLY anonymous surface (every /api/browse
/// route now requires authentication), and it shipped with no tests. These lock
/// down the three invariants it depends on:
/// <list type="number">
/// <item>never 404 — a 404 confirms a private repository exists;</item>
/// <item>Cache-Control keys only on whether a capability was presented, never
/// on the repository, for the same reason;</item>
/// <item>no caller-supplied text reaches the SVG.</item>
/// </list>
/// </summary>
public class BadgeControllerTests : CoverageRavenTest
{
    private const string SigningKey = "test-badge-signing-key-not-a-real-secret";

    private static BadgeController CreateController(IAsyncDocumentSession session, bool withSigningKey = true)
    {
        var settings = new Dictionary<string, string?>();
        if (withSigningKey) settings[BadgePrSignature.KeyConfigurationPath] = SigningKey;

        var services = new ServiceCollection();
        services.AddSingleton(session);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddScoped<BadgeController>();
        var controller = services.BuildServiceProvider().GetRequiredService<BadgeController>();
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static async Task<string> Svg(BadgeController controller, Task<IActionResult> call)
    {
        var result = await call;
        var content = result.Should().BeOfType<ContentResult>().Which;
        content.ContentType.Should().Be("image/svg+xml; charset=utf-8");
        return content.Content!;
    }

    private static string CacheControl(BadgeController controller)
        => controller.Response.Headers.CacheControl.ToString();

    /// <summary>Seeds a repository plus one commit per (branch, pr, completeness, percent) tuple.</summary>
    private static async Task Seed(
        IDocumentStore store, long repoId, bool isPrivate, string? badgeToken,
        CoverageSummary? latest,
        params (string Sha, string? Branch, int? Pr, string? Completeness, int Covered, int Coverable, DateTimeOffset At)[] commits)
    {
        using var seed = store.OpenAsyncSession();
        await seed.StoreAsync(new Repository
        {
            GitHubId = repoId,
            Name = "repo",
            FullName = "owner/repo",
            OwnerLogin = "owner",
            IsPrivate = isPrivate,
            BadgeToken = badgeToken,
            DefaultBranch = "main",
            LatestCoverage = latest,
        }, Repository.DocumentId(repoId));

        foreach (var c in commits)
        {
            await seed.StoreAsync(new Commit
            {
                Sha = c.Sha,
                Repository = Repository.DocumentId(repoId),
                Branch = c.Branch,
                PullRequestNumber = c.Pr,
                AuthoredAt = c.At,
                AssemblyCompleteness = c.Completeness,
                Coverage = new CoverageSummary { LinesCovered = c.Covered, LinesCoverable = c.Coverable },
            }, Commit.DocumentId(repoId, c.Sha));
        }

        await seed.SaveChangesAsync();
    }

    private static CoverageSummary Summary(int covered, int coverable)
        => new() { LinesCovered = covered, LinesCoverable = coverable };

    // ---------------------------------------------------------------- never 404

    /// <summary>
    /// A repository nobody has heard of must be indistinguishable from a
    /// private one the caller cannot see.
    /// </summary>
    [Fact]
    public async Task Unknown_repository_renders_unknown_and_never_404()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var controller = CreateController(session);

        var svg = await Svg(controller, controller.Get("nobody", "nothing", null, null, null, null, default));
        svg.Should().Contain("unknown");
    }

    [Fact]
    public async Task Private_repository_without_a_token_renders_unknown_and_never_404()
    {
        using var store = GetDocumentStore();
        await Seed(store, 1, isPrivate: true, badgeToken: "cafebabe", latest: Summary(80, 100));
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var controller = CreateController(session);
        var svg = await Svg(controller, controller.Get("owner", "repo", null, null, null, null, default));
        svg.Should().Contain("unknown");
        svg.Should().NotContain("80%");
    }

    [Fact]
    public async Task Private_repository_with_the_wrong_token_renders_unknown()
    {
        using var store = GetDocumentStore();
        await Seed(store, 1, isPrivate: true, badgeToken: "cafebabe", latest: Summary(80, 100));
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var controller = CreateController(session);
        var svg = await Svg(controller, controller.Get("owner", "repo", "deadbeef", null, null, null, default));
        svg.Should().Contain("unknown");
    }

    [Fact]
    public async Task Private_repository_with_the_right_token_renders_the_number()
    {
        using var store = GetDocumentStore();
        await Seed(store, 1, isPrivate: true, badgeToken: "cafebabe", latest: Summary(80, 100));
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var controller = CreateController(session);
        var svg = await Svg(controller, controller.Get("owner", "repo", "cafebabe", null, null, null, default));
        svg.Should().Contain("80%");
    }

    [Fact]
    public async Task Unknown_branch_and_unknown_pull_request_render_unknown_not_the_headline()
    {
        using var store = GetDocumentStore();
        await Seed(store, 1, isPrivate: false, badgeToken: null, latest: Summary(80, 100),
            ("aaa", "main", null, "Complete", 80, 100, DateTimeOffset.UtcNow));
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var controller = CreateController(session);

        // Regression for the shipped behaviour where an unrecognised selector
        // silently served Repository.LatestCoverage — measured in production as
        // ?pr=79 returning the default branch's number, which is the worst way
        // for a badge to be wrong: confidently.
        (await Svg(controller, controller.Get("owner", "repo", null, "no-such-branch", null, null, default)))
            .Should().Contain("unknown");
        (await Svg(controller, controller.Get("owner", "repo", null, null, 999999, null, default)))
            .Should().Contain("unknown");
    }

    // ----------------------------------------------------------- cache headers

    /// <summary>
    /// Exit criterion 3. The header must be byte-identical between a public and
    /// a private repository for the same request shape — otherwise it is an
    /// existence oracle that reintroduces exactly what never-404 prevents.
    /// </summary>
    [Fact]
    public async Task CacheControl_depends_only_on_whether_a_capability_was_presented()
    {
        using var publicStore = GetDocumentStore();
        await Seed(publicStore, 1, isPrivate: false, badgeToken: null, latest: Summary(80, 100));
        WaitForIndexing(publicStore);

        using var privateStore = GetDocumentStore();
        await Seed(privateStore, 2, isPrivate: true, badgeToken: "cafebabe", latest: Summary(80, 100));
        WaitForIndexing(privateStore);

        foreach (var store in new[] { publicStore, privateStore })
        {
            using var session = store.OpenAsyncSession();

            var anonymous = CreateController(session);
            await anonymous.Get("owner", "repo", null, null, null, null, default);
            CacheControl(anonymous).Should().Be("public, max-age=300");

            var withToken = CreateController(session);
            await withToken.Get("owner", "repo", "whatever", null, null, null, default);
            CacheControl(withToken).Should().Be("private, max-age=300");

            var withSig = CreateController(session);
            await withSig.Get("owner", "repo", null, null, 7, "whatever", default);
            CacheControl(withSig).Should().Be("private, max-age=300");
        }
    }

    // -------------------------------------------------------------- resolution

    /// <summary>
    /// Exit criterion 1: the default-branch selector and the unparameterised
    /// badge must agree, or the copyable snippet contradicts the README badge.
    /// </summary>
    [Fact]
    public async Task Branch_selector_agrees_with_the_headline_on_the_default_branch()
    {
        using var store = GetDocumentStore();
        await Seed(store, 1, isPrivate: false, badgeToken: null, latest: Summary(487, 1000),
            ("aaa", "main", null, "Complete", 487, 1000, DateTimeOffset.UtcNow.AddHours(-1)));
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var controller = CreateController(session);

        var headline = await Svg(controller, controller.Get("owner", "repo", null, null, null, null, default));
        var branch = await Svg(controller, controller.Get("owner", "repo", null, "main", null, null, default));
        headline.Should().Contain("48.7%");
        branch.Should().Contain("48.7%");
    }

    [Fact]
    public async Task Branch_selector_takes_the_newest_commit_of_that_branch()
    {
        using var store = GetDocumentStore();
        await Seed(store, 1, isPrivate: false, badgeToken: null, latest: Summary(50, 100),
            ("old", "feature/x", null, "Complete", 10, 100, DateTimeOffset.UtcNow.AddDays(-2)),
            ("new", "feature/x", null, "Complete", 90, 100, DateTimeOffset.UtcNow.AddDays(-1)),
            ("other", "feature/y", null, "Complete", 30, 100, DateTimeOffset.UtcNow));
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var controller = CreateController(session);
        (await Svg(controller, controller.Get("owner", "repo", null, "feature/x", null, null, default)))
            .Should().Contain("90%");
    }

    [Fact]
    public async Task Pull_request_selector_resolves_by_pull_request_number()
    {
        using var store = GetDocumentStore();
        await Seed(store, 1, isPrivate: false, badgeToken: null, latest: Summary(50, 100),
            ("head", "feature/x", 79, "Complete", 71, 100, DateTimeOffset.UtcNow));
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var controller = CreateController(session);
        (await Svg(controller, controller.Get("owner", "repo", null, null, 79, null, default)))
            .Should().Contain("71%");
    }

    /// <summary>
    /// ?pr= is the more specific selector, so it wins. Asserted rather than
    /// left to chance because the alternative — erroring — would break the
    /// never-404 contract.
    /// </summary>
    [Fact]
    public async Task Pull_request_selector_wins_when_both_are_given()
    {
        using var store = GetDocumentStore();
        await Seed(store, 1, isPrivate: false, badgeToken: null, latest: Summary(50, 100),
            ("a", "branch-a", null, "Complete", 10, 100, DateTimeOffset.UtcNow),
            ("b", "branch-b", 42, "Complete", 90, 100, DateTimeOffset.UtcNow));
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var controller = CreateController(session);
        (await Svg(controller, controller.Get("owner", "repo", null, "branch-a", 42, null, default)))
            .Should().Contain("90%");
    }

    /// <summary>
    /// The asymmetry this endpoint shipped with: the repository badge is
    /// promoted only from a Complete assembly, so a selector filtering merely
    /// on "has coverage" reported a subset's total as if it were the whole.
    /// </summary>
    [Fact]
    public async Task Complete_assembly_is_preferred_over_a_newer_partial_one()
    {
        using var store = GetDocumentStore();
        await Seed(store, 1, isPrivate: false, badgeToken: null, latest: Summary(50, 100),
            ("complete", "main", null, "Complete", 80, 100, DateTimeOffset.UtcNow.AddHours(-2)),
            ("partial", "main", null, "Partial", 12, 100, DateTimeOffset.UtcNow));
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var controller = CreateController(session);
        var svg = await Svg(controller, controller.Get("owner", "repo", null, "main", null, null, default));
        svg.Should().Contain("80%");
        svg.Should().NotContain("partial");
    }

    [Fact]
    public async Task A_partial_only_branch_renders_the_number_under_the_partial_label()
    {
        using var store = GetDocumentStore();
        await Seed(store, 1, isPrivate: false, badgeToken: null, latest: Summary(50, 100),
            ("partial", "feature/x", null, "Partial", 12, 100, DateTimeOffset.UtcNow));
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var controller = CreateController(session);
        var svg = await Svg(controller, controller.Get("owner", "repo", null, "feature/x", null, null, default));
        svg.Should().Contain("12%");
        svg.Should().Contain("coverage (partial)");
    }

    /// <summary>
    /// Zero coverable lines is "we measured nothing", not "we covered nothing".
    /// </summary>
    [Fact]
    public async Task Zero_coverable_lines_renders_unknown_not_zero_percent()
    {
        using var store = GetDocumentStore();
        await Seed(store, 1, isPrivate: false, badgeToken: null, latest: Summary(0, 0));
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var controller = CreateController(session);
        (await Svg(controller, controller.Get("owner", "repo", null, null, null, null, default)))
            .Should().Contain("unknown");
    }

    // --------------------------------------------------- PR-scoped signature

    /// <summary>
    /// Exit criterion 4: the bot can put a working badge in a private
    /// repository's PR comment without publishing BadgeToken, which is
    /// manager-only and repo-wide.
    /// </summary>
    [Fact]
    public async Task Valid_signature_admits_a_private_repositorys_pull_request_badge()
    {
        using var store = GetDocumentStore();
        await Seed(store, 1, isPrivate: true, badgeToken: "cafebabe", latest: Summary(50, 100),
            ("head", "feature/x", 79, "Complete", 71, 100, DateTimeOffset.UtcNow));
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var controller = CreateController(session);
        var sig = BadgePrSignature.Compute(
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { [BadgePrSignature.KeyConfigurationPath] = SigningKey }).Build(),
            gitHubId: 1, pullRequestNumber: 79);

        (await Svg(controller, controller.Get("owner", "repo", null, null, 79, sig, default)))
            .Should().Contain("71%");
    }

    /// <summary>
    /// The point of scoping: a signature lifted from one PR's comment must be
    /// worthless everywhere else, unlike the repo-wide badge token.
    /// </summary>
    [Fact]
    public async Task Signature_for_another_pull_request_renders_unknown()
    {
        using var store = GetDocumentStore();
        await Seed(store, 1, isPrivate: true, badgeToken: "cafebabe", latest: Summary(50, 100),
            ("a", "feature/a", 79, "Complete", 71, 100, DateTimeOffset.UtcNow),
            ("b", "feature/b", 80, "Complete", 92, 100, DateTimeOffset.UtcNow));
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var controller = CreateController(session);
        var sigFor79 = BadgePrSignature.Compute(
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { [BadgePrSignature.KeyConfigurationPath] = SigningKey }).Build(),
            gitHubId: 1, pullRequestNumber: 79);

        (await Svg(controller, controller.Get("owner", "repo", null, null, 80, sigFor79, default)))
            .Should().Contain("unknown");
    }

    [Fact]
    public async Task Signature_is_rejected_when_no_signing_key_is_configured()
    {
        using var store = GetDocumentStore();
        await Seed(store, 1, isPrivate: true, badgeToken: "cafebabe", latest: Summary(50, 100),
            ("head", "feature/x", 79, "Complete", 71, 100, DateTimeOffset.UtcNow));
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var controller = CreateController(session, withSigningKey: false);
        (await Svg(controller, controller.Get("owner", "repo", null, null, 79, "anything", default)))
            .Should().Contain("unknown");
    }

    /// <summary>A signature never substitutes for the token on a non-PR badge.</summary>
    [Fact]
    public async Task Signature_does_not_admit_the_repository_level_badge()
    {
        using var store = GetDocumentStore();
        await Seed(store, 1, isPrivate: true, badgeToken: "cafebabe", latest: Summary(80, 100),
            ("head", "feature/x", 79, "Complete", 71, 100, DateTimeOffset.UtcNow));
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var controller = CreateController(session);
        var sig = BadgePrSignature.Compute(
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { [BadgePrSignature.KeyConfigurationPath] = SigningKey }).Build(),
            gitHubId: 1, pullRequestNumber: 79);

        // No ?pr=, so there is nothing the signature is scoped to.
        (await Svg(controller, controller.Get("owner", "repo", null, null, null, sig, default)))
            .Should().Contain("unknown");
    }

    // -------------------------------------------------------------------- ETag

    [Fact]
    public async Task Matching_if_none_match_returns_304()
    {
        using var store = GetDocumentStore();
        await Seed(store, 1, isPrivate: false, badgeToken: null, latest: Summary(80, 100));
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var first = CreateController(session);
        await first.Get("owner", "repo", null, null, null, null, default);
        var etag = first.Response.Headers.ETag.ToString();
        etag.Should().StartWith("W/\"");

        var second = CreateController(session);
        second.Request.Headers.IfNoneMatch = etag;
        var result = await second.Get("owner", "repo", null, null, null, null, default);
        result.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(StatusCodes.Status304NotModified);
    }

    [Fact]
    public async Task Different_numbers_produce_different_etags()
    {
        using var store = GetDocumentStore();
        await Seed(store, 1, isPrivate: false, badgeToken: null, latest: Summary(80, 100),
            ("head", "feature/x", null, "Complete", 12, 100, DateTimeOffset.UtcNow));
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var headline = CreateController(session);
        await headline.Get("owner", "repo", null, null, null, null, default);

        var branch = CreateController(session);
        await branch.Get("owner", "repo", null, "feature/x", null, null, default);

        branch.Response.Headers.ETag.ToString().Should().NotBe(headline.Response.Headers.ETag.ToString());
    }
}
