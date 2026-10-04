using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Migrations;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;

namespace MintPlayer.Spark.Authorization.Migrations;

/// <summary>
/// Points stored <see cref="SparkUser"/> and <see cref="SparkRole"/> documents at the assembly that now
/// holds them (#388).
///
/// The user and role document model moved from MintPlayer.Spark.Authorization to
/// MintPlayer.Spark.Authorization.Abstractions, so an entity library can reference a user without
/// referencing ASP.NET Core. The namespace is unchanged, but <c>@metadata.Raven-Clr-Type</c> records
/// <c>Namespace.Type, Assembly</c>. Typed loads ignore it; Spark's untyped loads (row security,
/// breadcrumbs) do not, and fall back to a JObject when the recorded type no longer resolves.
///
/// Only the exact old value is rewritten, so an application's own subclass (<c>AppUser : SparkUser</c>,
/// recorded under the app's assembly) is left alone, and a second run changes nothing. Shipped by the
/// package and registered by each application's generated <c>AddMigrations()</c>.
/// </summary>
public partial class M_202610041800_UserDocumentsMovedAssembly : ISparkMigration
{
    public static long Version => 202610041800;
    public static string? Description => "SparkUser/SparkRole moved to MintPlayer.Spark.Authorization.Abstractions: rewrite Raven-Clr-Type";

    private const string OldAssembly = "MintPlayer.Spark.Authorization";

    [Inject] private readonly IDocumentStore store;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        await RewriteAsync(typeof(SparkUser), cancellationToken);
        await RewriteAsync(typeof(SparkRole), cancellationToken);
    }

    private async Task RewriteAsync(Type type, CancellationToken cancellationToken)
    {
        var collection = store.Conventions.FindCollectionName(type);
        var operation = await store.Operations.SendAsync(
            new PatchByQueryOperation(new IndexQuery
            {
                Query = $"from '{collection}' update {{ if (this['@metadata']['Raven-Clr-Type'] === $old) {{ this['@metadata']['Raven-Clr-Type'] = $new; }} }}",
                QueryParameters = new Parameters
                {
                    ["old"] = $"{type.FullName}, {OldAssembly}",
                    ["new"] = store.Conventions.FindClrTypeName(type),
                },
            },
            // Wait for the index rather than throw "Index is stale": a bulk operation on a stale index is refused outright.
            new QueryOperationOptions { StaleTimeout = TimeSpan.FromMinutes(5) }),
            token: cancellationToken);

        // Wait, so a throw here aborts startup and the migration is retried on the next start
        // rather than being marked done half-applied.
        await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(5));
    }
}
