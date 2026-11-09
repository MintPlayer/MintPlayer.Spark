using System.Net;
using System.Text.Json;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Tests._Infrastructure;
using Xunit;

namespace CodeCoverage.Tests.Controllers;

/// <summary>
/// "Run the scheduled slot now" (dependency-updates PRD R28, R21) starts a run (SDK and package updates alike) only on a
/// repository the caller manages: a manager of its account, or an administrator of that one repository on the forge. Any
/// other signed-in caller gets exactly the answer of a repository that does not exist, and nothing is queued; an anonymous
/// caller gets the uniform refusal.
/// </summary>
/// <remarks>
/// Through the real endpoint (<c>POST /spark/actions/execute</c>, the detail-page invocation with a <c>parent</c>) of the
/// real composition root, on <see cref="OwnerPrivacyHostFixture"/> (a <c>SparkTestDriver</c>): the owner manages acme
/// (open, secret, admin-only), the repository admin administers acme/open and acme/admin-only only, the member reaches
/// acme read-only, the CodeCoverage administrator and the stranger manage nothing, and user B owns globex. "Queued" is
/// read from the database: the action's <c>BroadcastOnceAsync</c> stores one <c>SparkMessages/dependency-run-now_…</c>
/// document per start.
/// </remarks>
[Collection(CoverageWebHostCollection.Name)]
public class RunScheduledSlotNowEndToEndTests(OwnerPrivacyHostFixture host) : IClassFixture<OwnerPrivacyHostFixture>
{
    private const string RepositoryTypeId = "22880468-80f5-4fd2-9472-4f87842ce4ff";
    private const string RunNowMessages = "SparkMessages/dependency-run-now_";

    private Task<(HttpStatusCode Status, string Body)> RunNowAsync(string repositoryId, string? user)
        => host.PostAsync("/spark/actions/execute", new
        {
            objectTypeId = RepositoryTypeId,
            actionName = "RunScheduledSlotNow",
            parent = new { id = repositoryId, objectTypeId = RepositoryTypeId, name = "Repository" },
        }, user);

    private async Task<int> QueuedAsync()
    {
        using var session = host.Store.OpenAsyncSession();
        return (await session.Advanced.LoadStartingWithAsync<object>(RunNowMessages, pageSize: 1024)).Count();
    }

    private static string Outcome(string body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("result").GetProperty("outcome").GetString()!;
    }

    private string Id(string name) => name switch
    {
        "open" => host.PublicRepositoryId,
        "secret" => host.PrivateRepositoryId,
        "admin-only" => host.AdministeredRepositoryId,
        "b-secret" => host.OtherRepositoryIds[0],
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    public static TheoryData<string, string> Managers => new()
    {
        { OwnerPrivacyHostFixture.OwnerUserId, "secret" },
        { OwnerPrivacyHostFixture.RepoAdminUserId, "admin-only" },
    };

    [Theory]
    [MemberData(nameof(Managers))]
    public async Task A_manager_of_the_repository_starts_exactly_one_run(string user, string repository)
    {
        var before = await QueuedAsync();

        var (status, body) = await RunNowAsync(Id(repository), user);

        status.Should().Be(HttpStatusCode.OK, body);
        Outcome(body).Should().Be("started");
        (await QueuedAsync()).Should().Be(before + 1);
    }

    /// <summary>Signed-in callers who do not manage the repository, whether or not they may read it.</summary>
    public static TheoryData<string, string> NonManagers => new()
    {
        // B cannot even read acme/secret.
        { OwnerPrivacyHostFixture.OtherOwnerUserId, "secret" },
        // Administers acme/open and acme/admin-only, not acme/secret: the forge answer is per repository.
        { OwnerPrivacyHostFixture.RepoAdminUserId, "secret" },
        // Reaches acme read-only: seeing a private repository is not managing it (R39).
        { OwnerPrivacyHostFixture.MemberUserId, "secret" },
        // Public repository: anyone may read its page, nobody else may run it.
        { OwnerPrivacyHostFixture.StrangerUserId, "open" },
        { OwnerPrivacyHostFixture.OtherOwnerUserId, "open" },
        // The CodeCoverage administrator manages no account it does not own on the forge.
        { OwnerPrivacyHostFixture.AdminUserId, "open" },
    };

    /// <remarks>
    /// The framework refuses before the action runs: Repository's write row filter, which applies to every action but
    /// Query and Read (<c>RepositoryActions.GetRowFilterAsync</c>, R39), admits only the owners the caller manages and the
    /// repositories they administer, and <c>CustomActionExecution</c> answers a row it rejects like a missing one (M-3).
    /// The action's own <c>CheckAsync</c> is the second gate (R21) behind it.
    /// </remarks>
    [Theory]
    [MemberData(nameof(NonManagers))]
    public async Task A_caller_who_does_not_manage_the_repository_gets_the_missing_repository_answer_and_nothing_is_queued(string user, string repository)
    {
        var before = await QueuedAsync();

        var (status, body) = await RunNowAsync(Id(repository), user);
        var (missingStatus, missing) = await RunNowAsync(Repository.DocumentId(EForgeProvider.GitHub, 4799), user);

        status.Should().Be(HttpStatusCode.NotFound, body);
        missingStatus.Should().Be(status);
        body.Should().Be(missing, "no existence oracle: a repository the caller does not manage and one that does not exist are refused alike");
        body.Should().NotContain("acme/");
        (await QueuedAsync()).Should().Be(before);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused_and_nothing_is_queued()
    {
        var before = await QueuedAsync();

        var (status, body) = await RunNowAsync(host.PublicRepositoryId, user: null);

        status.Should().NotBe(HttpStatusCode.OK, body);
        ((int)status).Should().BeLessThan(500, body);
        (await QueuedAsync()).Should().Be(before);
    }

    [Fact]
    public async Task B_passes_the_check_on_their_own_repository_where_the_feature_is_off()
    {
        // globex/b-secret is B's (B owns globex), seeded with dependency updates Disabled: the answer is "off", not
        // "refused". This proves the refusals above come from not managing the repository, not from a blanket deny.
        var before = await QueuedAsync();

        var (status, body) = await RunNowAsync(Id("b-secret"), OwnerPrivacyHostFixture.OtherOwnerUserId);

        status.Should().Be(HttpStatusCode.OK, body);
        Outcome(body).Should().Be("off");
        (await QueuedAsync()).Should().Be(before);
    }
}
