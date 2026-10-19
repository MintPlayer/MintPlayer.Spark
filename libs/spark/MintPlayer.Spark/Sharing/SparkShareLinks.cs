using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Sharing;
using MintPlayer.Spark.Endpoints.PersistentObject;
using MintPlayer.Spark.Endpoints.Queries;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Sharing;

/// <summary>
/// Share links (<c>docs/seo_analyzer_PRD.md</c> X6, D31): a capability token per object. Who may create or revoke
/// one is the application's rights (<c>ShareLink/{Type}</c>, <c>RevokeShareLink/{Type}</c>) and row rules (with
/// organizations: <c>[RequiresOrganizationRole("ShareLink", OrganizationRole.Admin)]</c>); this service only does it.
/// </summary>
/// <remarks>
/// Every <c>typeName</c> names the shareable type by its model id, alias or name, or by its CLR class's simple name
/// (<c>nameof(Site)</c>); the model type is found by <c>clrType</c>, so an application rename (D28) changes nothing.
/// Links carry the model id.
/// </remarks>
public interface ISparkShareLinks
{
    /// <summary>Whether links to <paramref name="typeName"/> may be made and read now (<c>Spark:Share:Enabled</c> and every <see cref="ISparkShareGate"/>).</summary>
    ValueTask<bool> IsEnabledAsync(string typeName, CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="IsEnabledAsync(string, CancellationToken)"/> for the type, then every gate's
    /// <see cref="ISparkShareGate.IsEnabledForAsync"/> for the stored object <paramref name="id"/>; false when it does not exist.
    /// </summary>
    ValueTask<bool> IsEnabledAsync(string typeName, string id, CancellationToken cancellationToken = default);

    /// <summary>Makes a new link for the object, replacing (revoking) any earlier one. Returns the token; it is never stored.</summary>
    /// <exception cref="InvalidOperationException">The type is not declared with <c>AddShareLinks</c>, or the object does not exist.</exception>
    Task<string> CreateAsync(string typeName, string id, CancellationToken cancellationToken = default);

    /// <summary>Revokes the object's link; its holders get the unknown-link answer at once.</summary>
    Task RevokeAsync(string typeName, string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether <paramref name="token"/> is the current link of the object <paramref name="id"/> and the link may be read
    /// now: the same check the share page's read makes (declared type, switch and gates, fixed-time hash compare), for an
    /// application's own anonymous surface on a link, such as a badge image.
    /// </summary>
    Task<bool> IsValidAsync(string typeName, string id, string token, CancellationToken cancellationToken = default);

    /// <summary>The page the link opens, absolute when the public base URL (or, for its creator, the request) is known.</summary>
    string LinkFor(string typeName, string id, string token, HttpContext? context);
}

/// <summary>
/// What a link shows: the declared attributes of one object and the first page of each declared child query, with the
/// type's label and each shown query's label (translated as on any page) so the page can head them.
/// </summary>
public sealed record SharedObject(
    string Id,
    string Type,
    TranslatedString? Label,
    IReadOnlyList<PersistentObjectAttribute> Attributes,
    IReadOnlyDictionary<string, QueryResult> Queries,
    IReadOnlyDictionary<string, TranslatedString?> QueryLabels);

internal sealed class SparkShareLinks(
    IDocumentStore store,
    IServiceScopeFactory scopes,
    IModelLoader modelLoader,
    IQueryLoader queryLoader,
    IOptions<SparkShareOptions> options,
    IEnumerable<ISparkShareGate> gates,
    IConfiguration? configuration = null,
    IHostEnvironment? environment = null) : ISparkShareLinks
{
    public ValueTask<bool> IsEnabledAsync(string typeName, CancellationToken cancellationToken = default)
        => Find(typeName) is { } target ? IsEnabledAsync(target, cancellationToken) : ValueTask.FromResult(false);

    private async ValueTask<bool> IsEnabledAsync(Target target, CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
            return false;
        foreach (var gate in gates)
            if (!await gate.IsEnabledAsync(target.EntityType.Name, cancellationToken))
                return false;
        return true;
    }

    public async ValueTask<bool> IsEnabledAsync(string typeName, string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(id) || Find(typeName) is not { } target || !await IsEnabledAsync(target, cancellationToken))
            return false;
        using var session = store.OpenAsyncSession();
        return await LoadAsync(session, target.Definition.ClrType, id, cancellationToken) is { } entity
            && await IsEnabledForAsync(target.EntityType.Name, entity, cancellationToken);
    }

    private async ValueTask<bool> IsEnabledForAsync(string typeName, object entity, CancellationToken cancellationToken)
    {
        foreach (var gate in gates)
            if (!await gate.IsEnabledForAsync(typeName, entity, cancellationToken))
                return false;
        return true;
    }

    public async Task<string> CreateAsync(string typeName, string id, CancellationToken cancellationToken = default)
    {
        var target = Find(typeName) ?? throw new InvalidOperationException($"'{typeName}' is not shareable: declare it with spark.AddShareLinks(o => o.Share<…>(…)).");
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        await SetHashAsync(target, id, Hash(token), cancellationToken);
        return token;
    }

    public async Task RevokeAsync(string typeName, string id, CancellationToken cancellationToken = default)
    {
        var target = Find(typeName) ?? throw new InvalidOperationException($"'{typeName}' is not shareable.");
        await SetHashAsync(target, id, null, cancellationToken);
    }

    public string LinkFor(string typeName, string id, string token, HttpContext? context)
    {
        // The type travels by its model id (D28): an application rename changes the name and a derived alias, never
        // the id, and a recipient holds the link for as long as it is not revoked.
        var type = Find(typeName)?.EntityType.Id.ToString("D") ?? typeName;
        var path = options.Value.LinkPath.StartsWith('/') ? options.Value.LinkPath : "/" + options.Value.LinkPath;
        var relative = $"{path.TrimEnd('/')}/{Uri.EscapeDataString(type)}/{Uri.EscapeDataString(token)}?id={Uri.EscapeDataString(id)}";
        // The link is shown to its creator only (never mailed by Spark), so outside Development the request's own
        // origin is an acceptable fallback when no public base URL is configured: a forged Host fools only its sender.
        var origin = SparkPublicBaseUrl.Resolve(options.Value.PublicBaseUrl, configuration, environment, context)
            ?? (context is null ? null : SparkPublicBaseUrl.RequestOrigin(context));
        return origin + relative;
    }

    /// <summary>The object behind a valid link, as its link shows it; null for every refusal alike (unknown type, id or token, revoked, disabled, expired).</summary>
    public async Task<bool> IsValidAsync(string typeName, string id, string token, CancellationToken cancellationToken = default)
        => await ResolveAsync(typeName, id, token, cancellationToken) is not null;

    internal async Task<SharedObject?> ReadAsync(string typeName, string id, string token, HttpContext context, CancellationToken cancellationToken)
    {
        if (await ResolveAsync(typeName, id, token, cancellationToken) is not { } shared)
            return null;

        return await AsShareAsync(shared, context, async services =>
        {
            var readPath = services.GetRequiredService<IPersistentObjectReadPath>();
            var po = await readPath.LoadAsync(new PersistentObjectReferenceRequest { ObjectTypeId = shared.EntityType.Id.ToString(), Id = id }, cancellationToken);
            if (po is null)
                return null;

            var queries = new Dictionary<string, QueryResult>(StringComparer.OrdinalIgnoreCase);
            var labels = new Dictionary<string, TranslatedString?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, _) in shared.Definition.Queries)
            {
                if (await RunChildAsync(services, shared, name, 0, Math.Min(25, options.Value.MaxTake), null, cancellationToken) is { } page)
                {
                    queries[name] = page;
                    labels[name] = queryLoader.ResolveQuery(name)?.Label;
                }
            }
            return new SharedObject(
                id,
                shared.EntityType.Name,
                shared.EntityType.Label,
                po.Attributes.Where(a => shared.Definition.Attributes.Contains(a.Name)).ToList(),
                queries,
                labels);
        });
    }

    /// <summary>Another page (or sort) of one declared child query of the object behind a valid link; null for every refusal alike.</summary>
    internal async Task<QueryResult?> QueryAsync(string typeName, string id, string token, string queryName, int skip, int take, SortColumn[]? sort, HttpContext context, CancellationToken cancellationToken)
    {
        if (await ResolveAsync(typeName, id, token, cancellationToken) is not { } shared || !shared.Definition.Queries.ContainsKey(queryName))
            return null;
        return await AsShareAsync(shared, context, services => RunChildAsync(services, shared, queryName, Math.Max(0, skip), Math.Clamp(take, 1, options.Value.MaxTake), sort, cancellationToken));
    }

    /// <summary>A shareable type: what its links show, and its persistent-object type in this application's model.</summary>
    private sealed record Target(SparkShareDefinition Definition, EntityTypeDefinition EntityType);

    private sealed record Shared(SparkShareDefinition Definition, EntityTypeDefinition EntityType, string Id);

    /// <summary>
    /// The shareable type <paramref name="type"/> names (D28): its model id (what a link carries), alias or name, else
    /// the CLR class's simple name, which is what links made before D28 carry (<c>/shared/Site/…</c>) and what an
    /// application passes as <c>nameof(Site)</c>. The model type is found by <c>clrType</c>, never by assuming it is
    /// named like the class. <see langword="null"/> when the type is not declared with <c>AddShareLinks</c>.
    /// </summary>
    private Target? Find(string? type)
    {
        if (string.IsNullOrEmpty(type))
            return null;

        if (modelLoader.ResolveEntityType(type) is { ClrType: { } clrType } entityType
            && options.Value.Types.Values.FirstOrDefault(d => d.ClrType.FullName == clrType) is { } definition)
            return new Target(definition, entityType);

        var legacy = options.Value.Types.Values.FirstOrDefault(d => string.Equals(d.ClrType.Name, type, StringComparison.OrdinalIgnoreCase));
        return legacy is not null && modelLoader.GetEntityTypeByClrType(legacy.ClrType.FullName ?? legacy.ClrType.Name) is { } legacyType
            ? new Target(legacy, legacyType)
            : null;
    }

    /// <summary>The token check: the type declared and enabled, the object stored with this token's hash.</summary>
    private async Task<Shared?> ResolveAsync(string typeName, string id, string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(token) || token.Length > 128
            || Find(typeName) is not { } target || !await IsEnabledAsync(target, cancellationToken))
            return null;

        using var session = store.OpenAsyncSession();
        var loaded = await LoadAsync(session, target.Definition.ClrType, id, cancellationToken);
        if (loaded is not ISparkShareable shareable || shareable.ShareTokenHash is not { Length: > 0 } stored)
            return null;
        if (!await IsEnabledForAsync(target.EntityType.Name, loaded, cancellationToken))
            return null;
        var presented = Encoding.ASCII.GetBytes(Hash(token));
        var expected = Encoding.ASCII.GetBytes(stored);
        return CryptographicOperations.FixedTimeEquals(presented, expected) ? new Shared(target.Definition, target.EntityType, id) : null;
    }

    /// <summary>
    /// Runs <paramref name="read"/> in a fresh scope whose permission service answers only the share's rights,
    /// under a system principal (row policies that bypass the system context are skipped; expiration is not).
    /// </summary>
    private async Task<T?> AsShareAsync<T>(Shared shared, HttpContext context, Func<IServiceProvider, Task<T?>> read) where T : class
    {
        // Rights name the type as this application's model does, which need not be the CLR class's name (D28).
        var definition = shared.Definition;
        var resources = new List<string> { "Read/" + shared.EntityType.Name };
        foreach (var name in definition.Queries.Keys)
            if (queryLoader.ResolveQuery(name) is { EntityType: { } entityType })
                resources.Add("Query/" + entityType);

        var caller = context.User;
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(SparkSystemContext.ClaimType, "share")], "SparkShare"));
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<SparkShareGrant>().Activate(resources);
            return await read(scope.ServiceProvider);
        }
        finally
        {
            context.User = caller;
        }
    }

    private async Task<QueryResult?> RunChildAsync(IServiceProvider services, Shared shared, string name, int skip, int take, SortColumn[]? sort, CancellationToken cancellationToken)
    {
        var query = queryLoader.ResolveQuery(name);
        // Only a query the type declares as its own sub-query ("queries" on the persistent object) runs, and always
        // with the shared object as its parent: a share never lists anything else.
        if (query is null || !shared.EntityType.Queries.Any(q => Same(q.Query, query)))
            return null;

        var outcome = await services.GetRequiredService<IQueryReadPath>().ExecuteAsync(new ExecuteQueryRequest
        {
            QueryId = query.Id.ToString(),
            ParentId = shared.Id,
            ParentType = shared.EntityType.Id.ToString(),
            Skip = skip,
            Take = take,
            SortColumns = sort,
        }, cancellationToken);
        if (outcome.Result is not { } result)
            return null;

        var allowed = shared.Definition.Queries[name];
        return new QueryResult
        {
            Columns = result.Columns.Where(c => allowed.Contains(c.Name)).ToList(),
            Items = result.Items.Select(item => new QueryResultItem
            {
                Id = item.Id,
                Values = item.Values.Where(v => allowed.Contains(v.Key)).ToList(),
            }).ToList(),
            TotalItems = result.TotalItems,
            Skip = result.Skip,
            Take = result.Take,
        };

        static bool Same(string reference, SparkQuery q)
            => string.Equals(reference, q.Name, StringComparison.OrdinalIgnoreCase)
               || string.Equals(reference, q.Alias, StringComparison.OrdinalIgnoreCase)
               || string.Equals(reference, q.Id.ToString(), StringComparison.OrdinalIgnoreCase);
    }



    private async Task SetHashAsync(Target target, string id, string? hash, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var loaded = await LoadAsync(session, target.Definition.ClrType, id, cancellationToken);
        if (loaded is not ISparkShareable shareable)
            throw new InvalidOperationException($"No {target.EntityType.Name} '{id}'.");
        shareable.ShareTokenHash = hash;
        await session.SaveChangesAsync(cancellationToken);
    }

    private static async Task<object?> LoadAsync(Raven.Client.Documents.Session.IAsyncDocumentSession session, Type clrType, string id, CancellationToken cancellationToken)
    {
        var method = typeof(Raven.Client.Documents.Session.IAsyncDocumentSession).GetMethods()
            .Single(m => m.Name == nameof(session.LoadAsync) && m.IsGenericMethodDefinition && m.GetParameters() is [{ ParameterType: var p }, { ParameterType: var c }] && p == typeof(string) && c == typeof(CancellationToken))
            .MakeGenericMethod(clrType);
        var task = (Task)method.Invoke(session, [id, cancellationToken])!;
        await task;
        var loaded = task.GetType().GetProperty("Result")!.GetValue(task);
        return loaded is not null && clrType.IsInstanceOfType(loaded) ? loaded : null;
    }

    internal static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
