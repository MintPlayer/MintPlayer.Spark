using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;

namespace MintPlayer.Spark.Services;

[Register(typeof(IPermissionService), ServiceLifetime.Scoped)]
internal partial class PermissionService : IPermissionService
{
    // Always non-null: AddSpark registers the security.json evaluator unconditionally, and
    // authorization is no longer something an application can forget to switch on. The
    // "null => allow" branch this replaced was the original fail-open path.
    [Inject] private readonly IAccessControl accessControl;

    /// <summary>
    /// One decision per resource per request.
    /// </summary>
    /// <remarks>
    /// <b>Request-scoped, never process-wide.</b> The evaluator reads authentication state from
    /// <c>IHttpContextAccessor</c>, so a shared cache would answer one caller's question with
    /// another caller's identity. This service is already Scoped, which is what makes an instance
    /// field safe — do not make it static, and do not promote the service's lifetime.
    /// <para>
    /// Worth having because several endpoints ask the same question in a loop:
    /// <c>EntityTypes/List</c> asks once per type, <c>GetAliases</c> and <c>ProgramUnits/Get</c>
    /// walk the same model, <c>GetPermissions</c> asks five questions about one type, and query
    /// pruning multiplies all of it again.
    /// </para>
    /// <para>
    /// A plain <see cref="Dictionary{TKey, TValue}"/>: a request is single-threaded through these
    /// call sites, and a concurrent one would suggest otherwise.
    /// </para>
    /// </remarks>
    private readonly Dictionary<string, bool> decisions = new(StringComparer.OrdinalIgnoreCase);

    public async Task EnsureAuthorizedAsync(string action, string target, CancellationToken cancellationToken = default)
    {
        var resource = $"{action}/{target}";
        if (!await DecideAsync(resource, cancellationToken))
            throw new SparkAccessDeniedException(resource);
    }

    public Task<bool> IsAllowedAsync(string action, string target, CancellationToken cancellationToken = default)
        => DecideAsync($"{action}/{target}", cancellationToken);

    private async Task<bool> DecideAsync(string resource, CancellationToken cancellationToken)
    {
        if (decisions.TryGetValue(resource, out var cached))
            return cached;

        var allowed = await accessControl.IsAllowedAsync(resource, cancellationToken);
        decisions[resource] = allowed;
        return allowed;
    }
}
