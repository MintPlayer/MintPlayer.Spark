using System.Net;
using System.Text;
using System.Text.Json;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Octokit;
using Octokit.Internal;
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
    /// <summary>GitHub's <c>target_type</c> of an installation on an organization, and on a user account.</summary>
    private const string Organization = "Organization", User = "User";

    private static Dictionary<string, string> Levels(params (string Name, string Level)[] levels)
        => levels.ToDictionary(l => l.Name, l => l.Level, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void A_raised_level_is_pending()
    {
        var pending = GitHubInstallationPermissions.ComputePending(
            Levels(("contents", "write"), ("checks", "write")),
            Levels(("contents", "read"), ("checks", "write")), Organization);

        pending.Should().HaveCount(1);
        pending[0].Should().Be(new PendingPermission("contents", "Contents", "write", "read"));
    }

    [Fact]
    public void A_permission_the_installation_never_granted_is_pending_with_no_accepted_level()
    {
        var pending = GitHubInstallationPermissions.ComputePending(
            Levels(("actions", "write"), ("workflows", "write"), ("metadata", "read")),
            Levels(("metadata", "read")), Organization);

        pending.Select(p => p.Name).Should().Equal(["actions", "workflows"]);
        pending.Should().AllSatisfy(p => p.Accepted.Should().BeNull());
    }

    [Fact]
    public void An_equal_or_higher_accepted_level_is_not_pending()
    {
        var pending = GitHubInstallationPermissions.ComputePending(
            Levels(("contents", "read"), ("statuses", "write"), ("administration", "write")),
            Levels(("contents", "write"), ("statuses", "write"), ("administration", "admin")), Organization);

        pending.Should().BeEmpty();
    }

    [Fact]
    public void A_declared_none_is_never_pending()
    {
        GitHubInstallationPermissions.ComputePending(Levels(("pages", "none")), Levels(), Organization).Should().BeEmpty();
    }

    [Fact]
    public void Admin_outranks_write()
    {
        var pending = GitHubInstallationPermissions.ComputePending(
            Levels(("administration", "admin")), Levels(("administration", "write")), Organization);

        pending.Select(p => p.Name).Should().Equal(["administration"]);
    }

    [Fact]
    public void Permission_names_the_typed_client_does_not_know_are_compared_too()
    {
        // merge_queues is not one of Octokit 14's InstallationPermissions properties; comparing the raw
        // payloads is what keeps it from vanishing from both sides.
        var pending = GitHubInstallationPermissions.ComputePending(
            Levels(("merge_queues", "write")), Levels(("merge_queues", "read")), Organization);

        pending.Should().HaveCount(1);
        pending[0].DisplayName.Should().Be("Merge queues");
    }

    [Fact]
    public void An_unknown_level_is_pending_only_when_the_two_sides_differ()
    {
        GitHubInstallationPermissions.ComputePending(Levels(("future", "triage")), Levels(("future", "triage")), Organization)
            .Should().BeEmpty();
        GitHubInstallationPermissions.ComputePending(Levels(("future", "triage")), Levels(("future", "read")), Organization)
            .Should().HaveCount(1);
    }

    [Fact]
    public void Pending_permissions_are_ordered_by_name()
    {
        var pending = GitHubInstallationPermissions.ComputePending(
            Levels(("workflows", "write"), ("actions", "write"), ("checks", "write")), Levels(), Organization);

        pending.Select(p => p.Name).Should().Equal(["actions", "checks", "workflows"]);
    }

    /// <summary>
    /// §0 D76: account permissions are granted per user, when someone authorizes the App, so no installation ever holds
    /// one. Both spellings: GitHub's App payloads say <c>emails</c> and <c>keys</c>, the token request's schema
    /// <c>email_addresses</c> and <c>git_ssh_keys</c>. Enterprise permissions, which all start with <c>enterprise_</c>,
    /// belong to installations on an enterprise.
    /// </summary>
    [Theory]
    [InlineData(Organization)]
    [InlineData(User)]
    public void Account_and_enterprise_permissions_are_never_pending(string targetType)
    {
        var declared = Levels(
            ("emails", "read"), ("email_addresses", "read"), ("keys", "read"), ("git_ssh_keys", "read"), ("plan", "read"),
            ("enterprise_teams", "read"), ("enterprise_permission_github_adds_later", "read"));

        GitHubInstallationPermissions.ComputePending(declared, Levels(), targetType).Should().BeEmpty();
    }

    /// <summary>
    /// §0 D76: an organization holds organization permissions as well as repository ones, a user account repository
    /// permissions only. GitHub's <c>target_type</c> missing counts as a user account, as the installation webhook
    /// reads any value but <c>Organization</c>.
    /// </summary>
    [Theory]
    [InlineData(Organization, new[] { "contents", "members", "organization_projects" })]
    [InlineData(User, new[] { "contents" })]
    [InlineData(null, new[] { "contents" })]
    public void Organization_permissions_are_pending_on_an_organization_only(string? targetType, string[] expected)
    {
        var pending = GitHubInstallationPermissions.ComputePending(
            Levels(("contents", "write"), ("members", "read"), ("organization_projects", "write")), Levels(), targetType);

        pending.Select(p => p.Name).Should().Equal(expected);
    }

    /// <summary>
    /// §0 D76: a name the tables do not know may be organization-only, as <c>members</c>, <c>issue_types</c> and
    /// <c>secret_scanning_dismissal_requests</c> are without an <c>organization_</c> prefix. An organization holds
    /// both kinds, so it is asked. A user account is asked only once its installation holds the permission, which
    /// shows it can: otherwise a notice could ask it for something it could never accept.
    /// </summary>
    [Fact]
    public void A_name_the_tables_do_not_know_is_asked_of_a_user_account_only_once_its_installation_holds_it()
    {
        var declared = Levels(("permission_github_adds_later", "write"));

        GitHubInstallationPermissions.ComputePending(declared, Levels(), Organization)
            .Select(p => p.Name).Should().Equal(["permission_github_adds_later"]);
        GitHubInstallationPermissions.ComputePending(declared, Levels(), User).Should().BeEmpty();
        GitHubInstallationPermissions.ComputePending(declared, Levels(("permission_github_adds_later", "read")), User)
            .Select(p => p.Name).Should().Equal(["permission_github_adds_later"]);
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

    /// <summary>
    /// §0 D70: the two GitHub reads through Octokit's own transport, a <see cref="GitHubClient"/> over
    /// <see cref="HttpClientAdapter"/> with only the network replaced. For a JSON reply the adapter keeps the body as text
    /// in <c>HttpResponse.Body</c>, and <c>GetRaw</c>'s typed <c>byte[]</c> body stays empty. Reading only the latter threw
    /// "The input does not contain any JSON tokens" on every call, which was logged as a warning and cached as "nothing
    /// pending", so the notice never showed (found live, 2026-10-14). The stubs above never went through the transport.
    /// </summary>
    [Fact]
    public async Task The_pending_permissions_are_read_through_octokits_own_transport()
    {
        var github = new GitHubApi();
        github.Replies["/app"] = (HttpStatusCode.OK, """
            { "id": 1, "slug": "coverage", "html_url": "https://github.com/apps/coverage",
              "permissions": { "metadata": "read", "contents": "write", "actions": "write", "workflows": "write" } }
            """);
        github.Replies["/app/installations/42"] = (HttpStatusCode.OK, """
            { "id": 42, "app_id": 1, "target_type": "Organization",
              "html_url": "https://github.com/organizations/acme/settings/installations/42",
              "permissions": { "metadata": "read", "contents": "write" } }
            """);
        var logger = new RecordingLogger();

        var result = await CreateService(new StubInstallations(() => OctokitClient(github)), logger: logger)
            .GetPendingAsync(new Account { Login = "acme", InstallationId = 42 });

        logger.Entries.Should().BeEmpty();
        result.Should().NotBeNull();
        result!.HtmlUrl.Should().Be("https://github.com/organizations/acme/settings/installations/42");
        result.Pending.Should().Equal([
            new PendingPermission("actions", "Actions", "write", null),
            new PendingPermission("workflows", "Workflows", "write", null),
        ]);
        github.Paths.Should().Equal(["/app", "/app/installations/42"]);
    }

    /// <summary>
    /// §0 D76: the App declares repository, organization and account permissions, and an installation can accept only
    /// what its account type holds: repository permissions, plus organization permissions on an organization. Account
    /// permissions are granted per user, when someone authorizes the App. Shaped like the live payloads of 2026-10-14,
    /// where comparing every declared permission asked the organization for <c>emails</c>, and the user account for
    /// <c>emails</c>, <c>members</c> and <c>organization_projects</c>, besides the real <c>discussions</c>: a notice that
    /// accepting could never clear. With <c>discussions</c> accepted too, neither has anything pending.
    /// </summary>
    [Theory]
    [InlineData(Organization, false)]
    [InlineData(User, false)]
    [InlineData(Organization, true)]
    [InlineData(User, true)]
    public async Task An_installation_is_asked_only_for_the_permissions_its_account_type_can_accept(string targetType, bool discussionsAccepted)
    {
        var accepted = new Dictionary<string, string>
        {
            ["actions"] = "write", ["checks"] = "write", ["contents"] = "write", ["issues"] = "write",
            ["metadata"] = "read", ["pull_requests"] = "write", ["statuses"] = "write", ["workflows"] = "write",
        };
        if (targetType == Organization)
        {
            accepted["members"] = "read";
            accepted["organization_projects"] = "write";
        }
        if (discussionsAccepted)
            accepted["discussions"] = "read";

        var github = new GitHubApi();
        github.Replies["/app"] = (HttpStatusCode.OK, """
            { "id": 1, "slug": "coverage", "html_url": "https://github.com/apps/coverage",
              "permissions": {
                "actions": "write", "checks": "write", "contents": "write", "discussions": "read", "issues": "write",
                "metadata": "read", "pull_requests": "write", "statuses": "write", "workflows": "write",
                "members": "read", "organization_projects": "write",
                "emails": "read" } }
            """);
        github.Replies["/app/installations/42"] = (HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            id = 42,
            app_id = 1,
            target_type = targetType,
            html_url = targetType == Organization
                ? "https://github.com/organizations/acme/settings/installations/42"
                : "https://github.com/settings/installations/42",
            permissions = accepted,
        }));
        var logger = new RecordingLogger();

        var result = await CreateService(new StubInstallations(() => OctokitClient(github)), logger: logger)
            .GetPendingAsync(new Account { Login = "acme", InstallationId = 42 });

        logger.Entries.Should().BeEmpty();
        result.Should().NotBeNull();
        PendingPermission[] expected = discussionsAccepted ? [] : [new("discussions", "Discussions", "read", null)];
        result!.Pending.Should().Equal(expected);
    }

    /// <summary>
    /// §0 D70: a reply GitHub did give that this code cannot read fails the same way on every view, so it is logged as an
    /// error, where the empty-body defect hid as one warning a minute. An outage (here a 503 through the same transport)
    /// stays a warning. The page shows no notice either way.
    /// </summary>
    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    public async Task A_reply_that_cannot_be_read_is_an_error_and_an_outage_a_warning(string body)
    {
        var unreadable = new GitHubApi();
        unreadable.Replies["/app"] = (HttpStatusCode.OK, body);
        var unreadableLog = new RecordingLogger();
        var outage = new GitHubApi();
        outage.Replies["/app"] = (HttpStatusCode.ServiceUnavailable, """{ "message": "Service Unavailable" }""");
        var outageLog = new RecordingLogger();
        var account = new Account { Login = "acme", InstallationId = 42 };

        (await CreateService(new StubInstallations(() => OctokitClient(unreadable)), logger: unreadableLog).GetPendingAsync(account))
            .Should().BeNull();
        (await CreateService(new StubInstallations(() => OctokitClient(outage)), logger: outageLog).GetPendingAsync(account))
            .Should().BeNull();

        unreadableLog.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Error);
        outageLog.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
    }

    private static GitHubInstallationPermissions CreateService(IGitHubInstallationService installations, bool configured = true, ILogger<GitHubInstallationPermissions>? logger = null)
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
            logger ?? NullLogger<GitHubInstallationPermissions>.Instance);
    }

    /// <summary>The App client the way production builds one, with only the network replaced.</summary>
    private static IGitHubClient OctokitClient(HttpMessageHandler network)
        => new GitHubClient(new Connection(
            new ProductHeaderValue("CodeCoverage.Tests"), GitHubClient.GitHubApiUrl,
            new InMemoryCredentialStore(new Credentials("app-jwt", AuthenticationType.Bearer)),
            new HttpClientAdapter(() => network), new SimpleJsonSerializer()));

    /// <summary>GitHub's REST API as far as this class reads it: a reply per path, with GitHub's content type.</summary>
    private sealed class GitHubApi : HttpMessageHandler
    {
        public Dictionary<string, (HttpStatusCode Status, string Body)> Replies { get; } = new(StringComparer.Ordinal);
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            var (status, body) = Replies.TryGetValue(path, out var reply) ? reply : (HttpStatusCode.NotFound, """{ "message": "Not Found" }""");
            return Task.FromResult(new HttpResponseMessage(status)
            {
                RequestMessage = request,
                // application/json; charset=utf-8, as api.github.com sends it.
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class RecordingLogger : ILogger<GitHubInstallationPermissions>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
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
