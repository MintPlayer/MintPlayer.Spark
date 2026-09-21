using CodeCoverage.Forge;
using Xunit;

namespace CodeCoverage.Tests.Forge;

/// <summary>
/// The OIDC claim map, and the two places where forges genuinely disagree.
/// </summary>
/// <remarks>
/// A claim map is easy to write and easy to write wrongly, because every value is a string that
/// compiles either way. These pin the parts a second forge would otherwise quietly break.
/// </remarks>
public class ForgeOidcProfileTests
{
    /// <summary>
    /// The GitHub profile must keep saying exactly what GitHub emits. These are wire values with
    /// no compiler behind them: a typo authenticates nobody and looks like "OIDC is broken".
    /// </summary>
    [Fact]
    public void GitHub_claims_match_what_GitHub_emits()
    {
        var github = ForgeOidcProfile.GitHub;

        github.Issuer.Should().Be("https://token.actions.githubusercontent.com");
        github.RepositoryClaim.Should().Be("repository");
        github.RepositoryIdClaim.Should().Be("repository_id");
        github.OwnerClaim.Should().Be("repository_owner");
        github.OwnerIdClaim.Should().Be("repository_owner_id");
        github.VisibilityClaim.Should().Be("repository_visibility");
        github.RunIdClaim.Should().Be("run_id");
        github.RunAttemptClaim.Should().Be("run_attempt");
    }

    /// <summary>
    /// The abstraction's actual test: a second forge names every claim differently, so a profile
    /// that merely wrapped GitHub's constants would be indistinguishable from one that works.
    /// </summary>
    [Fact]
    public void Two_forges_share_no_claim_names()
    {
        var github = ForgeOidcProfile.GitHub;
        var gitlab = ForgeOidcProfile.GitLab;

        gitlab.RepositoryClaim.Should().NotBe(github.RepositoryClaim);
        gitlab.RepositoryIdClaim.Should().NotBe(github.RepositoryIdClaim);
        gitlab.OwnerClaim.Should().NotBe(github.OwnerClaim);
        gitlab.VisibilityClaim.Should().NotBe(github.VisibilityClaim);
        gitlab.RunIdClaim.Should().NotBe(github.RunIdClaim);
    }

    /// <summary>
    /// ⚠️ GitLab has no run <em>attempt</em> — it retries a job rather than re-running an attempt of
    /// a pipeline. The profile says so with null rather than inventing a claim name, and this pins
    /// that, because a build id derived from (run, attempt) needs a different second component
    /// there. A string that looked plausible would have been discovered at runtime, on a token that
    /// never carries it.
    /// </summary>
    [Fact]
    public void A_forge_without_run_attempts_says_so_rather_than_inventing_a_claim()
        => ForgeOidcProfile.GitLab.RunAttemptClaim.Should().BeNull();

    /// <summary>
    /// Visibility is compared by value, and auto-provisioning is gated on it — so the expected
    /// value belongs to the profile rather than being a literal at the comparison site. A forge
    /// that says "internal" or "PUBLIC" must be able to state that.
    /// </summary>
    [Fact]
    public void Public_visibility_is_a_value_each_forge_declares()
        => ForgeOidcProfile.GitHub.PublicVisibilityValue.Should().Be("public");

    /// <summary>
    /// Schemes must be distinct, or registering the second forge would silently replace the first's
    /// authentication handler.
    /// </summary>
    [Fact]
    public void Each_forge_authenticates_under_its_own_scheme()
        => new[] { ForgeOidcProfile.GitHub, ForgeOidcProfile.GitLab }
            .Select(p => p.SchemeName)
            .Should().OnlyHaveUniqueItems();
}
