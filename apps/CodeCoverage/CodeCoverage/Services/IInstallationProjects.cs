using Microsoft.Extensions.DependencyInjection;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Octokit;
using Octokit.GraphQL;
using static Octokit.GraphQL.Variable;

namespace CodeCoverage.Services;

/// <summary>One Projects V2 board as an installation reports it.</summary>
public sealed record InstallationProject(
    string NodeId,
    int Number,
    string Title,
    bool Closed);

/// <summary>
/// The Projects V2 boards a GitHub App installation can currently see.
/// <para>
/// A seam for the same reason as <see cref="IInstallationRepositories"/>: board reconciliation
/// disconnects boards that stop appearing, so its decisions have to be testable against an absent
/// board, a 404 and a transient failure without a live GitHub.
/// </para>
/// </summary>
public interface IInstallationProjects
{
    /// <summary>
    /// Every board the installation can see for <paramref name="ownerLogin"/>. Throws —
    /// <see cref="NotFoundException"/> and <see cref="AuthorizationException"/> mean the
    /// installation is gone; anything else means GitHub could not answer, which is not the same
    /// thing and must leave our state alone.
    /// </summary>
    Task<IReadOnlyList<InstallationProject>> ListAsync(
        long installationId, string ownerLogin, bool ownerIsOrganization, int max, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IInstallationProjects"/>
[Register(typeof(IInstallationProjects), ServiceLifetime.Scoped)]
public partial class InstallationProjects : IInstallationProjects
{
    [Inject] private readonly IGitHubInstallationService installations;

    /// <summary>
    /// GitHub's maximum page size for this connection. Measured cost for <c>first: 100</c> is
    /// <b>1 rate-limit point</b> (spike S1, 2026-09-07, measured with <c>rateLimit</c> selected in
    /// the same query document — asking for it separately measures the rate-limit query instead).
    /// </summary>
    private const int PageSize = 100;

    public async Task<IReadOnlyList<InstallationProject>> ListAsync(
        long installationId, string ownerLogin, bool ownerIsOrganization, int max, CancellationToken cancellationToken = default)
    {
        var connection = await installations.CreateGraphQLConnectionAsync(installationId, EClientType.Installation);

        // The owner type branch is unavoidable: `organization` and `user` are different root fields
        // with no common interface covering projectsV2, so one typed query cannot serve both. The
        // raw query this replaces branched the same way — what changed is that the login now travels
        // as a GraphQL **variable** rather than being interpolated into the document (defect B1).
        //
        // Verified against both shapes by spike S1: the app's two installations are a user account
        // and an organization, and the user one holds zero boards — so the empty result is the
        // common case here, not an edge case.
        var boards = ownerIsOrganization
            ? await RunAsync(connection, ownerLogin, org: true)
            : await RunAsync(connection, ownerLogin, org: false);

        // Not paged, deliberately, unlike IInstallationRepositories. That type pages because a
        // truncated repository list would make the reconciler disconnect a whole organization, and
        // installations genuinely hold hundreds of repositories. An owner with more than 100 project
        // boards is not a realistic shape for this app, and `first: 100` costs a single point — so
        // the bound is enforced rather than papered over: if an owner ever does exceed it, the
        // reconciler must not silently read the truncation as "these boards are gone".
        if (boards.Count > max)
            return boards.Take(max).ToList();

        return boards;
    }

    private static async Task<List<InstallationProject>> RunAsync(
        Octokit.GraphQL.Connection connection, string ownerLogin, bool org)
    {
        var variables = new Dictionary<string, object> { ["login"] = ownerLogin };

        if (org)
        {
            var query = new Query()
                .Organization(Var("login"))
                .ProjectsV2(first: PageSize)
                .Nodes
                .Select(p => new InstallationProject(p.Id.Value, p.Number, p.Title, p.Closed))
                .Compile();

            return (await connection.Run(query, variables)).ToList();
        }

        var userQuery = new Query()
            .User(Var("login"))
            .ProjectsV2(first: PageSize)
            .Nodes
            .Select(p => new InstallationProject(p.Id.Value, p.Number, p.Title, p.Closed))
            .Compile();

        return (await connection.Run(userQuery, variables)).ToList();
    }
}
