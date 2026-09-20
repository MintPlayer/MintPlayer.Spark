using Xunit;
using CodeCoverage.Services;

namespace CodeCoverage.Tests.Services;

/// <summary>
/// The owner set is what every authorization decision in the app is compared against
/// (<c>RepositoryVisibility</c>, <c>GitHubProjectVisibility</c>, the token actions), so what goes
/// into it is a security question rather than a formatting one.
/// </summary>
public class GitHubOwnerSetTests
{
    private static GitHubInstallation Installation(string login, bool suspended = false)
        => new(Id: 1, AccountGitHubId: 1, Login: login, Type: "Organization", AvatarUrl: null, Suspended: suspended);

    /// <summary>
    /// The D19 regression. A suspended installation used to keep conferring management rights,
    /// because this projection took every installation while the backfill on the same array
    /// filtered them — so suspending an installation revoked nothing.
    /// </summary>
    [Fact]
    public void Suspended_installations_confer_nothing()
    {
        var owners = GitHubAccessService.BuildOwnerSet(
            [Installation("ActiveOrg"), Installation("SuspendedOrg", suspended: true)],
            username: "someone");

        owners.Should().BeEquivalentTo(["ActiveOrg", "someone"]);
        owners.Should().NotContain("SuspendedOrg");
    }

    /// <summary>
    /// Suspending an installation must not cost a viewer their own repositories — the degraded
    /// paths fall back to exactly this login, and the two have to agree.
    /// </summary>
    [Fact]
    public void Own_login_survives_every_installation_being_suspended()
    {
        var owners = GitHubAccessService.BuildOwnerSet(
            [Installation("OrgA", suspended: true), Installation("OrgB", suspended: true)],
            username: "someone");

        owners.Should().BeEquivalentTo(["someone"]);
    }

    /// <summary>
    /// A user-account installation carries the same login as the viewer, so without the
    /// case-insensitive de-duplication the owner set would hold it twice and every
    /// <c>.In(owners)</c> comparison would widen its term list for no reason.
    /// </summary>
    [Fact]
    public void Own_login_is_not_duplicated_by_a_user_installation()
    {
        var owners = GitHubAccessService.BuildOwnerSet(
            [Installation("SomeOne")],
            username: "someone");

        owners.Should().ContainSingle();
    }

    /// <summary>
    /// An anonymous or claimless principal contributes no login, and must not contribute an empty
    /// string either — an empty owner would match an unset <c>OwnerLogin</c> on a stored document.
    /// </summary>
    [Fact]
    public void No_username_contributes_no_owner()
    {
        GitHubAccessService.BuildOwnerSet([Installation("OrgA")], username: null)
            .Should().BeEquivalentTo(["OrgA"]);

        GitHubAccessService.BuildOwnerSet([], username: null)
            .Should().BeEmpty();
    }
}
