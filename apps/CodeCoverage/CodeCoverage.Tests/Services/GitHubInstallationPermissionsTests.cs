using System.Net;
using System.Text.Json;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Octokit;
using Account = CodeCoverage.Entities.Account;
using Xunit;
using GraphQLConnection = Octokit.GraphQL.Connection;

namespace CodeCoverage.Tests.Services;

/// <summary>
/// Merge-queue PRD O17: which permissions the App declares that an installation has not accepted yet,
/// and that every way of having nothing to say degrades to "no notice" instead of an error.
/// </summary>
public class GitHubInstallationPermissionsTests
{
    private static Dictionary<string, string> Levels(params (string Name, string Level)[] levels)
        => levels.ToDictionary(l => l.Name, l => l.Level, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void A_raised_level_is_pending()
    {
        var pending = GitHubInstallationPermissions.ComputePending(
            Levels(("contents", "write"), ("checks", "write")),
            Levels(("contents", "read"), ("checks", "write")));

        pending.Should().HaveCount(1);
        pending[0].Should().Be(new PendingPermission("contents", "Contents", "write", "read"));
    }

    [Fact]
    public void A_permission_the_installation_never_granted_is_pending_with_no_accepted_level()
    {
        var pending = GitHubInstallationPermissions.ComputePending(
            Levels(("actions", "write"), ("workflows", "write"), ("metadata", "read")),
            Levels(("metadata", "read")));

        pending.Select(p => p.Name).Should().Equal(["actions", "workflows"]);
        pending.Should().AllSatisfy(p => p.Accepted.Should().BeNull());
    }

    [Fact]
    public void An_equal_or_higher_accepted_level_is_not_pending()
    {
        var pending = GitHubInstallationPermissions.ComputePending(
            Levels(("contents", "read"), ("statuses", "write"), ("administration", "write")),
            Levels(("contents", "write"), ("statuses", "write"), ("administration", "admin")));

        pending.Should().BeEmpty();
    }

    [Fact]
    public void A_declared_none_is_never_pending()
    {
        GitHubInstallationPermissions.ComputePending(Levels(("pages", "none")), Levels()).Should().BeEmpty();
    }

    [Fact]
    public void Admin_outranks_write()
    {
        var pending = GitHubInstallationPermissions.ComputePending(
            Levels(("administration", "admin")), Levels(("administration", "write")));

        pending.Select(p => p.Name).Should().Equal(["administration"]);
    }

    [Fact]
    public void Permission_names_the_typed_client_does_not_know_are_compared_too()
    {
        // merge_queues is not one of Octokit 14's InstallationPermissions properties; comparing the raw
        // payloads is what keeps it from vanishing from both sides.
        var pending = GitHubInstallationPermissions.ComputePending(
            Levels(("merge_queues", "write")), Levels(("merge_queues", "read")));

        pending.Should().HaveCount(1);
        pending[0].DisplayName.Should().Be("Merge queues");
    }

    [Fact]
    public void An_unknown_level_is_pending_only_when_the_two_sides_differ()
    {
        GitHubInstallationPermissions.ComputePending(Levels(("future", "triage")), Levels(("future", "triage")))
            .Should().BeEmpty();
        GitHubInstallationPermissions.ComputePending(Levels(("future", "triage")), Levels(("future", "read")))
            .Should().HaveCount(1);
    }

    [Fact]
    public void Pending_permissions_are_ordered_by_name()
    {
        var pending = GitHubInstallationPermissions.ComputePending(
            Levels(("workflows", "write"), ("actions", "write"), ("checks", "write")), Levels());

        pending.Select(p => p.Name).Should().Equal(["actions", "checks", "workflows"]);
    }

    [Fact]
    public void Permissions_are_read_from_the_raw_payload_skipping_non_string_values()
    {
        using var payload = JsonDocument.Parse("""
            { "id": 1, "html_url": "https://github.com/organizations/acme/settings/installations/1",
              "permissions": { "actions": "write", "pull_requests": "read", "weird": 3 } }
            """);

        var permissions = GitHubInstallationPermissions.ReadPermissions(payload.RootElement);

        permissions.Should().HaveCount(2);
        permissions["actions"].Should().Be("write");
        permissions["pull_requests"].Should().Be("read");
    }

    [Fact]
    public void A_payload_without_permissions_reads_as_none()
    {
        using var payload = JsonDocument.Parse("""{ "id": 1 }""");
        GitHubInstallationPermissions.ReadPermissions(payload.RootElement).Should().BeEmpty();
    }

    [Fact]
    public void Display_names_read_as_words()
    {
        GitHubInstallationPermissions.DisplayName("pull_requests").Should().Be("Pull requests");
        GitHubInstallationPermissions.DisplayName("actions").Should().Be("Actions");
    }

    [Fact]
    public async Task An_account_without_an_installation_has_no_notice_and_calls_nothing()
    {
        var installations = new StubInstallations(() => throw new InvalidOperationException("must not be called"));
        var result = await CreateService(installations).GetPendingAsync(new Account { Login = "acme", InstallationId = null });

        result.Should().BeNull();
        installations.Calls.Should().Be(0);
    }

    [Fact]
    public async Task An_unconfigured_app_has_no_notice_and_calls_nothing()
    {
        var installations = new StubInstallations(() => throw new InvalidOperationException("must not be called"));
        var result = await CreateService(installations, configured: false)
            .GetPendingAsync(new Account { Login = "acme", InstallationId = 42 });

        result.Should().BeNull();
        installations.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_missing_installation_degrades_to_no_notice()
    {
        var installations = new StubInstallations(() => throw new NotFoundException("Not Found", HttpStatusCode.NotFound));
        var result = await CreateService(installations).GetPendingAsync(new Account { Login = "acme", InstallationId = 42 });

        result.Should().BeNull();
    }

    [Fact]
    public async Task A_forge_failure_degrades_to_no_notice_and_is_cached()
    {
        var installations = new StubInstallations(() => throw new HttpRequestException("unreachable"));
        var service = CreateService(installations);
        var account = new Account { Login = "acme", InstallationId = 42 };

        (await service.GetPendingAsync(account)).Should().BeNull();
        (await service.GetPendingAsync(account)).Should().BeNull();
        installations.Calls.Should().Be(1);
    }

    private static GitHubInstallationPermissions CreateService(IGitHubInstallationService installations, bool configured = true)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(configured
            ? new Dictionary<string, string?>
            {
                ["GitHub:Production:PrivateKeyPath"] = "/run/secrets/github-app.pem",
                ["GitHub:Production:AppId"] = "12345",
            }
            : []).Build();
        return new GitHubInstallationPermissions(
            installations, new MemoryCache(new MemoryCacheOptions()), config, new StubEnvironment(),
            NullLogger<GitHubInstallationPermissions>.Instance);
    }

    private sealed class StubInstallations(Func<IGitHubClient> createAppClient) : IGitHubInstallationService
    {
        public int Calls { get; private set; }

        public Task<IGitHubClient> CreateAppClientAsync()
        {
            Calls++;
            return Task.FromResult(createAppClient());
        }

        public Task<IGitHubClient> CreateInstallationClientAsync(long installationId) => throw new NotSupportedException();
        public Task<GraphQLConnection> CreateGraphQLConnectionAsync(long installationId, EClientType clientType) => throw new NotSupportedException();
    }

    private sealed class StubEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "CodeCoverage.Tests";
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
