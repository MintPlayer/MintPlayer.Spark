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
    public async ValueTask<bool> IsEnabledAsync(string typeName, CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled || !options.Value.Types.ContainsKey(typeName))
            return false;
        foreach (var gate in gates)
            if (!await gate.IsEnabledAsync(typeName, cancellationToken))
                return false;
        return true;
    }

    public async ValueTask<bool> IsEnabledAsync(string typeName, string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(id) || !await IsEnabledAsync(typeName, cancellationToken) || Definition(typeName) is not { } definition)
            return false;
        using var session = store.OpenAsyncSession();
        return await LoadAsync(session, definition.ClrType, id, cancellationToken) is { } entity
            && await IsEnabledForAsync(definition.TypeName, entity, cancellationToken);
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
        var definition = Definition(typeName) ?? throw new InvalidOperationException($"'{typeName}' is not shareable: declare it with spark.AddShareLinks(o => o.Share<…>(…)).");
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        await SetHashAsync(definition, id, Hash(token), cancellationToken);
        return token;
    }

    public async Task RevokeAsync(string typeName, string id, CancellationToken cancellationToken = default)
    {
        var definition = Definition(typeName) ?? throw new InvalidOperationException($"'{typeName}' is not shareable.");
        await SetHashAsync(definition, id, null, cancellationToken);
    }

    public string LinkFor(string typeName, string id, string token, HttpContext? context)
    {
        var path = options.Value.LinkPath.StartsWith('/') ? options.Value.LinkPath : "/" + options.Value.LinkPath;
        var relative = $"{path.TrimEnd('/')}/{Uri.EscapeDataString(typeName)}/{Uri.EscapeDataString(token)}?id={Uri.EscapeDataString(id)}";
        // The link is shown to its creator only (never mailed by Spark), so outside Development the request's own
        // origin is an acceptable fallback when no public base URL is configured: a forged Host fools only its sender.
        var origin = SparkPublicBaseUrl.Resolve(options.Value.PublicBaseUrl, configuration, environment, context)
            ?? (context is null ? null : SparkPublicBaseUrl.RequestOrigin(context));
        return origin + relative;
    }

    /// <summary>The object behind a valid link, as its link shows it; null for every refusal alike (unknown type, id or token, revoked, disabled, expired).</summary>
    internal async Task<SharedObject?> ReadAsync(string typeName, string id, string token, HttpContext context, CancellationToken cancellationToken)
    {
        if (await ResolveAsync(typeName, id, token, cancellationToken) is not { } shared)
            return null;

        return await AsShareAsync(shared.Definition, context, async services =>
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
                shared.Definition.TypeName,
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
        return await AsShareAsync(shared.Definition, context, services => RunChildAsync(services, shared, queryName, Math.Max(0, skip), Math.Clamp(take, 1, options.Value.MaxTake), sort, cancellationToken));
    }

    private sealed record Shared(SparkShareDefinition Definition, EntityTypeDefinition EntityType, string Id);

    /// <summary>The token check: the type declared and enabled, the object stored with this token's hash.</summary>
    private async Task<Shared?> ResolveAsync(string typeName, string id, string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(token) || token.Length > 128
            || Definition(typeName) is not { } definition || !await IsEnabledAsync(definition.TypeName, cancellationToken)
            || modelLoader.GetEntityTypeByName(definition.TypeName) is not { } entityType)
            return null;

        using var session = store.OpenAsyncSession();
        var loaded = await LoadAsync(session, definition.ClrType, id, cancellationToken);
        if (loaded is not ISparkShareable shareable || shareable.ShareTokenHash is not { Length: > 0 } stored)
            return null;
        if (!await IsEnabledForAsync(definition.TypeName, loaded, cancellationToken))
            return null;
        var presented = Encoding.ASCII.GetBytes(Hash(token));
        var expected = Encoding.ASCII.GetBytes(stored);
        return CryptographicOperations.FixedTimeEquals(presented, expected) ? new Shared(definition, entityType, id) : null;
    }

    /// <summary>
    /// Runs <paramref name="read"/> in a fresh scope whose permission service answers only the share's rights,
    /// under a system principal (row policies that bypass the system context are skipped; expiration is not).
    /// </summary>
    private async Task<T?> AsShareAsync<T>(SparkShareDefinition definition, HttpContext context, Func<IServiceProvider, Task<T?>> read) where T : class
    {
        var resources = new List<string> { "Read/" + definition.TypeName };
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
            ParentType = shared.Definition.TypeName,
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



    private SparkShareDefinition? Definition(string typeName)
        => options.Value.Types.TryGetValue(typeName ?? "", out var definition) ? definition : null;

    private async Task SetHashAsync(SparkShareDefinition definition, string id, string? hash, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var loaded = await LoadAsync(session, definition.ClrType, id, cancellationToken);
        if (loaded is not ISparkShareable shareable)
            throw new InvalidOperationException($"No {definition.TypeName} '{id}'.");
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
