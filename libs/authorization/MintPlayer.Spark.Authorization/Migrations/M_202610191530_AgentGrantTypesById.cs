using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Agents;
using MintPlayer.Spark.Migrations;
using MintPlayer.Spark.Services;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;

namespace MintPlayer.Spark.Authorization.Migrations;

/// <summary>
/// Agent grants name their chosen types by id, no longer by name (D28, commerce X9): each <c>Types[i].Name</c> becomes
/// <c>Types[i].TypeId</c>, mapped through the composed model this application serves. A name is what an application's
/// <c>[assembly: PersistentObjectName]</c> changes; a grant stored by name would then grant nothing, or grant another
/// type that took the name. The work is <see cref="AgentGrantTypeUpgrade"/>.
/// </summary>
/// <remarks>
/// A name the model no longer knows is left as it is (with no <c>TypeId</c>, so it grants nothing: fail closed) and
/// logged; the user sees the grant on the account page and can choose its types again.
/// </remarks>
public partial class M_202610191530_AgentGrantTypesById : ISparkMigration
{
    public static string? Description => "Agent grants: chosen types by id, not by name (D28)";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly ILogger<M_202610191530_AgentGrantTypesById> logger;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        var unknown = await AgentGrantTypeUpgrade.RunAsync(store, AgentGrantTypeUpgrade.NameMap(modelLoader.GetEntityTypes()), cancellationToken);
        foreach (var (grantId, name) in unknown)
            logger.LogWarning("Agent grant {GrantId} chose the type '{Type}', which no model type has; it now grants nothing for it.", grantId, name);
    }
}

/// <summary>
/// The rewrite of <see cref="M_202610191530_AgentGrantTypesById"/>: <c>{"Name":"Car","Permissions":…}</c> →
/// <c>{"TypeId":"…","Permissions":…}</c> for every chosen type whose name the model knows.
/// </summary>
/// <remarks>
/// Idempotent: an element that has a <c>TypeId</c> is left alone, so a second run changes nothing and RavenDB writes
/// no document the script left unchanged. The script is a constant and the map a query parameter; the collection
/// name, which RQL cannot take as a parameter, is validated by <see cref="RqlIdentifier.Collection"/> (repo rule).
/// </remarks>
internal static class AgentGrantTypeUpgrade
{
    private const string Script = """
        update {
            if (this.Types) {
                for (var i = 0; i < this.Types.length; i++) {
                    var t = this.Types[i];
                    if (!t || t.TypeId || typeof t.Name !== 'string') continue;
                    if (Object.prototype.hasOwnProperty.call($map, t.Name)) {
                        t.TypeId = $map[t.Name];
                        delete t.Name;
                    }
                }
            }
        }
        """;

    /// <summary>Type name → id (<c>"D"</c>) of every model type.</summary>
    public static Dictionary<string, string> NameMap(IEnumerable<EntityTypeDefinition> types)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in types)
            map.TryAdd(type.Name, type.Id.ToString("D"));
        return map;
    }

    /// <returns>Each (grant id, stored name) the model does not know, left as it is.</returns>
    public static async Task<IReadOnlyList<(string GrantId, string Name)>> RunAsync(
        IDocumentStore store, IReadOnlyDictionary<string, string> nameToId, CancellationToken cancellationToken = default)
    {
        var collection = RqlIdentifier.Collection(store.Conventions.FindCollectionName(typeof(AgentGrant)));
        var unknown = new List<(string, string)>();
        var anyByName = false;

        using (var session = store.OpenAsyncSession())
        {
            // A collection scan, no index: nothing to wait for and nothing stale.
            var query = session.Advanced.AsyncRawQuery<GrantRow>($"from '{collection}' select id() as Id, Types");
            await using var stream = await session.Advanced.StreamAsync(query, cancellationToken);
            while (await stream.MoveNextAsync())
            {
                var row = stream.Current.Document;
                foreach (var type in row.Types ?? [])
                {
                    if (!string.IsNullOrEmpty(type.TypeId) || string.IsNullOrEmpty(type.Name)) continue;
                    anyByName = true;
                    if (!nameToId.ContainsKey(type.Name))
                        unknown.Add((row.Id ?? stream.Current.Id, type.Name));
                }
            }
        }

        if (!anyByName || nameToId.Count == 0)
            return unknown;

        var operation = await store.Operations.SendAsync(
            new PatchByQueryOperation(
                new IndexQuery
                {
                    Query = $"from '{collection}'\n{Script}",
                    QueryParameters = new Parameters { ["map"] = new Dictionary<string, string>(nameToId, StringComparer.Ordinal) },
                },
                new QueryOperationOptions { StaleTimeout = TimeSpan.FromMinutes(5) }),
            token: cancellationToken);

        // Waited, so a throw aborts startup and the migration is retried rather than marked done half-applied.
        await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(5));
        return unknown;
    }

    private sealed class GrantRow
    {
        public string? Id { get; set; }
        public List<LegacyType>? Types { get; set; }
    }

    /// <summary>A chosen type in either shape: <c>Name</c> before D28, <c>TypeId</c> after.</summary>
    private sealed class LegacyType
    {
        public string? Name { get; set; }
        public string? TypeId { get; set; }
    }
}
