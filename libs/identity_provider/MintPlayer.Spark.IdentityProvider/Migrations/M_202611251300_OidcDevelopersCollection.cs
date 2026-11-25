using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;

namespace MintPlayer.Spark.IdentityProvider.Migrations;

/// <summary>
/// Moves every user's developer status from the <c>Developer</c> field of the user document into a document of its own,
/// <c>oidcdevelopers/{sha256(user id)}</c> (<see cref="OidcDevelopers.DocumentId"/>, #536 D47), and drops the field.
/// <para>
/// The field was patched onto the user document and read with a projection, which only RavenDB can do: a relational
/// store keeps a user's declared members only. The status is a document of its own on every store now.
/// </para>
/// <para>
/// RavenDB only: the field was never written anywhere else. Idempotent: a status document that exists already is kept,
/// and a second run finds no field left.
/// </para>
/// </summary>
public partial class M_202611251300_OidcDevelopersCollection : ISparkMigration
{
    public static long Version => 202611251300;
    public static string? Description => "Identity provider: developer status moves from the user document into OidcDevelopers";

    private const string LegacyField = "Developer";
    // A RavenDB session allows 30 requests; one batch load and one save per chunk.
    private const int Chunk = 100;

    [Inject] private readonly IDocumentStore? raven;
    [Inject] private readonly ISparkStore store;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        if (raven is null || store.ProviderName != SparkStoreProviders.RavenDb)
            return;

        // Spliced, because RQL takes no parameter for a collection; validated, because an application can customise the
        // convention to return anything (#264). The field name is a constant of this migration.
        var collection = RqlIdentifier.Collection(raven.Conventions.FindCollectionName(typeof(SparkUser)));

        var legacy = new List<(string UserId, LegacyDeveloper Developer)>();
        using (var session = raven.OpenAsyncSession())
        {
            // A collection scan with a projection, no index: nothing to wait for, nothing stale, no auto index (D42).
            var query = session.Advanced.AsyncRawQuery<LegacyRow>($"from '{collection}' as u select {{ Id: id(u), Developer: u.{LegacyField} }}");
            await using var stream = await session.Advanced.StreamAsync(query, cancellationToken);
            while (await stream.MoveNextAsync())
                if (stream.Current.Document is { Developer: { } developer } row)
                    legacy.Add((row.Id ?? stream.Current.Id, developer));
        }

        foreach (var chunk in legacy.Chunk(Chunk))
        {
            using var session = store.OpenSession();
            var existing = await session.LoadAsync<OidcDeveloper>(chunk.Select(l => OidcDevelopers.DocumentId(l.UserId)), cancellationToken);
            foreach (var (userId, old) in chunk)
            {
                var id = OidcDevelopers.DocumentId(userId);
                if (existing.GetValueOrDefault(id) is not null)
                    continue;
                await session.StoreAsync(new OidcDeveloper
                {
                    Id = Id<OidcDeveloper>.FromTrusted(id),
                    UserId = userId,
                    Status = old.Status ?? OidcDeveloperStatuses.Requested,
                    TermsVersion = old.TermsVersion,
                    RequestedAt = old.RequestedAt,
                    DecidedAt = old.DecidedAt,
                    DecidedBy = old.DecidedBy,
                    Note = old.Note,
                }, id, cancellationToken);
            }
            await session.SaveChangesAsync(cancellationToken);
        }

        if (legacy.Count == 0)
            return;

        // The old field goes: RavenDB keeps a field the model does not declare for ever (PreserveDocumentPropertiesNotFoundOnModel).
        var operation = await raven.Operations.SendAsync(
            new PatchByQueryOperation(new IndexQuery { Query = $"from '{collection}' as u update {{ if (u.{LegacyField} !== undefined) {{ delete u.{LegacyField}; }} }}" },
                new QueryOperationOptions { StaleTimeout = TimeSpan.FromMinutes(5) }),
            token: cancellationToken);
        await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(5));
    }

    private sealed class LegacyRow
    {
        public string? Id { get; set; }
        public LegacyDeveloper? Developer { get; set; }
    }

    private sealed class LegacyDeveloper
    {
        public string? Status { get; set; }
        public int TermsVersion { get; set; }
        public DateTime RequestedAt { get; set; }
        public DateTime? DecidedAt { get; set; }
        public string? DecidedBy { get; set; }
        public string? Note { get; set; }
    }
}
