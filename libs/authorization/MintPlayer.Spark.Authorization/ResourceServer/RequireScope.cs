using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using MintPlayer.Spark.Authorization.Extensions;

namespace MintPlayer.Spark.Authorization.ResourceServer;

/// <summary>
/// The caller's access token must carry every one of <see cref="Scopes"/> (<c>docs/identity_provider_platform_PRD.md</c>
/// D8, I12). Read from the space-delimited <c>scope</c> claim (RFC 9068 §2.2.3) or <c>scp</c> claims.
/// </summary>
public sealed class SparkScopeRequirement(IReadOnlyList<string> scopes) : IAuthorizationRequirement
{
    public IReadOnlyList<string> Scopes { get; } = scopes;
}

/// <summary>
/// <c>[RequireScope("fleet.read")]</c> on an endpoint or controller: authenticated through the resource-server
/// scheme, and holding every scope named.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequireScopeAttribute : AuthorizeAttribute, IAuthorizationRequirementData
{
    public RequireScopeAttribute(params string[] scopes)
    {
        if (scopes.Length == 0 || scopes.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Name at least one scope.", nameof(scopes));
        Scopes = scopes;
        AuthenticationSchemes = SparkJwtBearerExtensions.Scheme;
    }

    public IReadOnlyList<string> Scopes { get; }

    public IEnumerable<IAuthorizationRequirement> GetRequirements() => [new SparkScopeRequirement(Scopes)];
}

internal sealed class SparkScopeRequirementHandler : AuthorizationHandler<SparkScopeRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, SparkScopeRequirement requirement)
    {
        var granted = context.User.FindAll("scope").SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Concat(context.User.FindAll("scp").Select(c => c.Value))
            .ToHashSet(StringComparer.Ordinal);
        if (requirement.Scopes.All(granted.Contains))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

public static class RequireScopeEndpointExtensions
{
    /// <summary>
    /// <c>app.MapGet(...).RequireScope("fleet.read")</c>: the endpoint answers only a caller authenticated through
    /// the resource-server scheme (<c>spark.AddSparkResourceServer(...)</c>) whose token carries every scope named.
    /// </summary>
    public static TBuilder RequireScope<TBuilder>(this TBuilder builder, params string[] scopes) where TBuilder : IEndpointConventionBuilder
        => builder.RequireAuthorization(new RequireScopeAttribute(scopes));
}
