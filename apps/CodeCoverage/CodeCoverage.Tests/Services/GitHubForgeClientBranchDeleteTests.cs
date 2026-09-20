using CodeCoverage.Entities;
using CodeCoverage.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace CodeCoverage.Tests.Services;

/// <summary>
/// Branch deletion's failure tolerance, tested where the tolerance actually lives.
/// </summary>
/// <remarks>
/// <para>
/// These assertions used to sit on <c>GitHubEventsRecipient</c>, because that class both decided to
/// delete and performed the delete. M8c split those: the <em>policy</em> (has the repository opted
/// in? is the head a fork's?) is neutral and moved to <c>ForgeEventsRecipient</c>, while the ref
/// delete and its error mapping are GitHub's and moved here. The test followed its subject rather
/// than being deleted with the code it used to cover.
/// </para>
/// <para>
/// <b>Why never-throws is worth a test rather than a comment.</b> This runs after a merge has
/// already landed. Throwing would cost the webhook delivery and change nothing about the merge — and
/// the three failure shapes below are ones GitHub genuinely produces, including a 404 that can mean
/// either "already gone" or a permission failure wearing the wrong status.
/// </para>
/// </remarks>
public class GitHubForgeClientBranchDeleteTests
{
    public static TheoryData<string, Exception> Failures => new()
    {
        { "403 Forbidden", new Octokit.ApiException(ResponseWith(System.Net.HttpStatusCode.Forbidden)) },
        { "404 NotFound", new Octokit.NotFoundException(ResponseWith(System.Net.HttpStatusCode.NotFound)) },
        { "an unexpected fault", new InvalidOperationException("boom") },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task A_failed_delete_never_throws(string shape, Exception failure)
    {
        var client = Create(new ThrowingInstallationService { Throws = failure });

        var act = async () => await client.DeleteBranchAsync(SampleRepository, "feature/thing");

        await act.Should().NotThrowAsync($"a {shape} must not cost the caller, which runs after the merge landed");
    }

    /// <summary>
    /// No installation means no credential, which is a reason to decline rather than to throw — the
    /// same distinction <c>CheckAccessAsync</c> draws.
    /// </summary>
    [Fact]
    public async Task No_installation_declines_quietly()
    {
        var installer = new ThrowingInstallationService();
        var client = Create(installer);

        // The repository has no account, so no installation can be resolved.
        await client.DeleteBranchAsync(new Repository { Id = "Repositories/9", OwnerLogin = "acme", Name = "widget" }, "x");

        installer.Created.Should().Be(0, "a missing credential must not produce an API call");
    }

    private static Repository SampleRepository
        => new() { Id = "Repositories/1", GitHubId = 1, OwnerLogin = "acme", Name = "widget" };

    private static GitHubForgeClient Create(ThrowingInstallationService installer)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        services.AddSingleton(Substitute.For<IGitHubDiffService>());
        services.AddSingleton(Substitute.For<IGitHubContentService>());
        services.AddSingleton(Substitute.For<Raven.Client.Documents.Session.IAsyncDocumentSession>());
        services.AddSingleton<MintPlayer.Spark.Webhooks.GitHub.Services.IGitHubInstallationService>(installer);
        services.AddScoped<GitHubForgeClient>();
        return services.BuildServiceProvider().GetRequiredService<GitHubForgeClient>();
    }

    private static Octokit.IResponse ResponseWith(System.Net.HttpStatusCode status)
    {
        var response = Substitute.For<Octokit.IResponse>();
        response.StatusCode.Returns(status);
        return response;
    }

    private sealed class ThrowingInstallationService : MintPlayer.Spark.Webhooks.GitHub.Services.IGitHubInstallationService
    {
        public Exception? Throws { get; set; }
        public int Created { get; private set; }

        public Task<Octokit.IGitHubClient> CreateInstallationClientAsync(long installationId)
        {
            Created++;
            var reference = Substitute.For<Octokit.IReferencesClient>();
            reference
                .When(r => r.Delete(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()))
                .Do(_ => { if (Throws is not null) throw Throws; });

            var git = Substitute.For<Octokit.IGitDatabaseClient>();
            git.Reference.Returns(reference);

            var client = Substitute.For<Octokit.IGitHubClient>();
            client.Git.Returns(git);
            return Task.FromResult(client);
        }

        public Task<Octokit.IGitHubClient> CreateAppClientAsync() => throw new NotSupportedException();

        public Task<Octokit.GraphQL.Connection> CreateGraphQLConnectionAsync(
            long installationId, MintPlayer.Spark.Webhooks.GitHub.Services.EClientType clientType)
            => throw new NotSupportedException();
    }
}
