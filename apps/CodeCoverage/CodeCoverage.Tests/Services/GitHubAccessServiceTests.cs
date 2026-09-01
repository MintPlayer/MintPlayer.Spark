using Xunit;
using CodeCoverage.Services;
using FluentAssertions;

namespace CodeCoverage.Tests.Services;

public class GitHubAccessServiceTests
{
    [Fact]
    public void ParseInstallations_extracts_id_account_and_suspension()
    {
        // Shape of GET /user/installations (trimmed to the consumed fields
        // plus representative noise).
        var json = """
            {
              "total_count": 3,
              "installations": [
                {
                  "id": 153409068,
                  "app_id": 4574022,
                  "target_type": "Organization",
                  "suspended_at": null,
                  "account": { "login": "MintPlayer", "id": 48772716, "type": "Organization", "avatar_url": "https://avatars.githubusercontent.com/u/48772716?v=4" }
                },
                {
                  "id": 153409070,
                  "app_id": 4574022,
                  "target_type": "User",
                  "account": { "login": "PieterjanDeClippel", "id": 9629574, "type": "User", "avatar_url": "https://avatars.githubusercontent.com/u/9629574?v=4" }
                },
                {
                  "id": 153409099,
                  "target_type": "Organization",
                  "suspended_at": "2026-08-01T00:00:00Z",
                  "account": { "login": "SuspendedOrg", "id": 42, "type": "Organization" }
                }
              ]
            }
            """;

        var result = GitHubAccessService.ParseInstallations(json);

        result.Should().HaveCount(3);
        result[0].Should().Be(new GitHubInstallation(
            153409068, 48772716, "MintPlayer", "Organization",
            "https://avatars.githubusercontent.com/u/48772716?v=4", Suspended: false));
        result[1].Should().Be(new GitHubInstallation(
            153409070, 9629574, "PieterjanDeClippel", "User",
            "https://avatars.githubusercontent.com/u/9629574?v=4", Suspended: false));
        result[2].Login.Should().Be("SuspendedOrg");
        result[2].Suspended.Should().BeTrue();
        result[2].AvatarUrl.Should().BeNull();
    }

    [Fact]
    public void ParseInstallations_skips_entries_missing_required_fields()
    {
        var json = """
            {
              "installations": [
                { "id": 1, "account": null },
                { "id": 2, "account": { "id": 7 } },
                { "id": 3, "account": { "login": "NoAccountId" } },
                { "account": { "login": "NoInstallationId", "id": 8 } },
                { "id": 4, "account": { "login": "Valid", "id": 9 } }
              ]
            }
            """;

        var result = GitHubAccessService.ParseInstallations(json);

        result.Should().ContainSingle()
            .Which.Should().Be(new GitHubInstallation(4, 9, "Valid", null, null, Suspended: false));
    }

    [Fact]
    public void ParseInstallations_returns_empty_when_installations_property_is_absent()
    {
        GitHubAccessService.ParseInstallations("""{"message":"Bad credentials"}""").Should().BeEmpty();
    }
}
