using CodeCoverage.Controllers;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Tests.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Controllers;

/// <summary>
/// What the Account page's "accept the new permissions" notice reads (merge-queue PRD O17, D78): 200 only when
/// something waits for acceptance; 204 when nothing does, or when nothing can be said; 404 for an account the
/// viewer does not manage. Which permissions count as pending is <see cref="GitHubInstallationPermissionsTests"/>'s
/// subject.
/// </summary>
public class AccountPermissionsControllerTests : CoverageRavenTest
{
    protected override bool DeployIndexes => true;

    private const string Owner = "acme";
    private const string InstallationPage = "https://github.com/organizations/acme/settings/installations/42";

    /// <summary>The installation's permissions, answering whatever the test says.</summary>
    private sealed class FixedPermissions(PendingInstallationPermissions? answer) : IForgeInstallationPermissions
    {
        public EForgeProvider Provider => EForgeProvider.GitHub;

        public Task<PendingInstallationPermissions?> GetPendingAsync(Account account, CancellationToken cancellationToken = default)
            => Task.FromResult(answer);
    }

    private static AccountPermissionsController Controller(
        IAsyncDocumentSession session, PendingInstallationPermissions? answer, string viewerManages = Owner)
    {
        var forge = new ScriptedDiffService();
        forge.Owners.Add(new ForgeOwner(EForgeProvider.GitHub, viewerManages));

        var services = new ServiceCollection();
        services.AddSingleton(session);
        services.AddSingleton<IForgeIntegrationResolver>(forge);
        services.AddSingleton<IForgeInstallationPermissions>(new FixedPermissions(answer));
        services.AddScoped<AccountPermissionsController>();
        return services.BuildServiceProvider().GetRequiredService<AccountPermissionsController>();
    }

    private async Task<IDocumentStore> SeededStoreAsync()
    {
        var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(
                new Account { GitHubId = 1, Login = Owner, Type = "Organization", InstallationId = 42 },
                Account.DocumentId(EForgeProvider.GitHub, 1));
            await seed.SaveChangesAsync();
        }

        WaitForIndexing(store);
        return store;
    }

    /// <summary>
    /// Nothing pending is the steady state of every installation that accepted its permissions, so it answers
    /// as "nothing to say" does: no body. The notice hides on either, and on an empty list too.
    /// </summary>
    [Theory]
    [InlineData(true)]  // the installation answered, and nothing waits for acceptance
    [InlineData(false)] // nothing can be said: no installation, or the forge unreachable
    public async Task Nothing_pending_answers_no_content(bool installationAnswered)
    {
        using var store = await SeededStoreAsync();
        using var session = store.OpenAsyncSession();
        var answer = installationAnswered ? new PendingInstallationPermissions(InstallationPage, []) : null;

        var result = await Controller(session, answer).Get("github", Owner, default);

        result.Result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task Pending_permissions_answer_ok_with_the_installations_page()
    {
        using var store = await SeededStoreAsync();
        using var session = store.OpenAsyncSession();
        var pending = new PendingInstallationPermissions(InstallationPage, [new PendingPermission("actions", "Actions", "write", "read")]);

        var result = await Controller(session, pending).Get("github", Owner, default);

        result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(pending);
    }

    /// <summary>The answer for an account that does not exist, so the endpoint confirms nothing to a non-manager.</summary>
    [Fact]
    public async Task An_account_the_viewer_does_not_manage_is_not_found()
    {
        using var store = await SeededStoreAsync();
        using var session = store.OpenAsyncSession();
        var pending = new PendingInstallationPermissions(InstallationPage, [new PendingPermission("actions", "Actions", "write", "read")]);

        var result = await Controller(session, pending, viewerManages: "someone-else").Get("github", Owner, default);

        result.Result.Should().BeOfType<NotFoundResult>();
    }
}
