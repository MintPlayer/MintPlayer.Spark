using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Authorization.Agents;

/// <summary>
/// The account page's <b>Connected applications</b> (MCP PRD D14): the user's agent grants and the entries other
/// packages contribute; editing a grant's types and read/changes; revoking; creating a personal access token.
/// Everything is the signed-in user's own: grant ids are rebuilt from the user id (<see cref="AgentGrantIds"/>).
/// </summary>
/// <remarks>
/// <b>What a user may grant.</b> At most what a call could use: the app layer for that agent
/// (<c>mcp.AgentPermissions</c>) and, per type, the type's <c>agentPermissions</c>, offered only for types the
/// user may see (<see cref="IAgentExposure"/>, implemented by the Mcp package). A request beyond that is refused,
/// not silently trimmed, so what the page shows is what was stored. The call still intersects every layer again
/// (D6): the grant only ever narrows.
/// </remarks>
internal sealed partial class AgentGrantsAccount(
    AgentGrantStore grants,
    AgentRequestUser users,
    IServiceProvider services,
    IEnumerable<ISparkConnectedApplicationSource> sources,
    TimeProvider time,
    ILogger<AgentGrantsAccount> logger)
{
    private const int MaxNameLength = 100;

    /// <summary>Whether the page has anything to show: the Mcp package (agents) or a contributing package.</summary>
    internal static bool IsAvailable(IServiceProvider root)
    {
        var known = root.GetService<IServiceProviderIsService>();
        return known is not null && (known.IsService(typeof(IAgentExposure)) || known.IsService(typeof(ISparkConnectedApplicationSource)));
    }

    private IAgentExposure? Exposure => services.GetService<IAgentExposure>();

    public async Task<AgentGrantsOverview> OverviewAsync(ClaimsPrincipal principal, string userId, CancellationToken cancellationToken)
    {
        var exposure = Exposure;
        var types = exposure is null ? [] : await exposure.ListTypesAsync(cancellationToken);
        var user = await users.GetAsync(userId, cancellationToken);
        var now = time.GetUtcNow();

        var views = (await grants.ListAsync(userId, cancellationToken))
            .OrderByDescending(g => g.CreatedAt)
            .Select(g => new AgentGrantView(
                AgentGrantIds.KeyOf(g),
                g.Kind.ToString(),
                g.Name,
                g.Kind == EAgentKind.PersonalAccessToken ? null : g.ClientId,
                g.VerifiedHost,
                g.Permissions,
                g.Types,
                exposure?.AppPermissions(AgentOf(g, principal)) ?? EAgentPermission.None,
                g.CreatedAt,
                g.ExpiresAt,
                g.LastUsedAt,
                StatusOf(g, user, now)))
            .ToList();

        var applications = new List<ConnectedApplicationView>();
        foreach (var source in sources)
        {
            foreach (var entry in await source.ListAsync(userId, cancellationToken))
                applications.Add(new ConnectedApplicationView(source.Name, entry.Id, entry.Name, entry.Description, entry.CreatedAt, entry.LastUsedAt));
        }

        var tokens = exposure is not null && exposure.Setups.HasFlag(EAgentAuthentication.PersonalAccessToken);
        return new AgentGrantsOverview(
            tokens,
            (int)PersonalAccessTokens.DefaultLifetime.TotalDays,
            (int)PersonalAccessTokens.MaxLifetime.TotalDays,
            tokens ? exposure!.AppPermissions(TokenAgent(principal, null, null)) : EAgentPermission.None,
            types,
            views,
            applications);
    }

    /// <summary>Creates a personal access token and its grant. The token is in the answer once and nowhere else.</summary>
    public async Task<AgentResult<AgentTokenCreated>> CreateTokenAsync(ClaimsPrincipal principal, string userId, AgentTokenRequest request, CancellationToken cancellationToken)
    {
        var exposure = Exposure;
        if (exposure is null || !exposure.Setups.HasFlag(EAgentAuthentication.PersonalAccessToken))
            return AgentResult<AgentTokenCreated>.Fail("agent_tokens_unavailable");

        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || name.Any(char.IsControl))
            return AgentResult<AgentTokenCreated>.Fail("agent_invalid_name");

        var days = request.ExpiresInDays ?? (int)PersonalAccessTokens.DefaultLifetime.TotalDays;
        if (days < 1 || days > PersonalAccessTokens.MaxLifetime.TotalDays)
            return AgentResult<AgentTokenCreated>.Fail("agent_invalid_lifetime");

        var user = await users.GetAsync(userId, cancellationToken);
        if (user is null)
            return AgentResult<AgentTokenCreated>.Fail("agent_unknown_user");

        var token = PersonalAccessTokens.Create(userId, out var tokenId);
        var max = exposure.AppPermissions(TokenAgent(principal, tokenId, name));
        if (await ValidateAsync(exposure, request.Permissions, request.Types, max, cancellationToken) is { } invalid)
            return AgentResult<AgentTokenCreated>.Fail(invalid);

        var now = time.GetUtcNow();
        var grant = new AgentGrant
        {
            Id = AgentGrantIds.PersonalAccessToken(userId, tokenId),
            UserId = userId,
            Kind = EAgentKind.PersonalAccessToken,
            ClientId = tokenId,
            Name = name,
            Permissions = request.Permissions,
            Types = Normalize(request.Types),
            CreatedAt = now,
            ExpiresAt = now.AddDays(days),
            TokenHash = PersonalAccessTokens.Hash(token),
            SecurityStamp = user.SecurityStamp ?? string.Empty,
        };
        await grants.SaveAsync(grant, cancellationToken);
        LogTokenCreated(logger, userId, grant.Id, grant.ExpiresAt.Value);

        return AgentResult<AgentTokenCreated>.Ok(new AgentTokenCreated(token, AgentGrantIds.KeyOf(grant), grant.ExpiresAt.Value));
    }

    /// <summary>Changes a grant's types and read/changes; the agent's next call uses them.</summary>
    public async Task<AgentResult<bool>> UpdateAsync(ClaimsPrincipal principal, string userId, AgentGrantUpdateRequest request, CancellationToken cancellationToken)
    {
        if (AgentGrantIds.FromKey(userId, request.Key) is not { } id || await grants.GetOwnAsync(userId, id, cancellationToken) is not { } grant)
            return AgentResult<bool>.NotFound();

        var exposure = Exposure;
        if (exposure is null)
            return AgentResult<bool>.Fail("agent_tokens_unavailable");

        var max = exposure.AppPermissions(AgentOf(grant, principal));
        if (await ValidateAsync(exposure, request.Permissions, request.Types, max, cancellationToken) is { } invalid)
            return AgentResult<bool>.Fail(invalid);

        grant.Permissions = request.Permissions;
        grant.Types = Normalize(request.Types);
        await grants.SaveAsync(grant, cancellationToken);
        LogGrantChanged(logger, userId, grant.Id!, grant.Permissions.ToString(), grant.Types is null ? "all" : string.Join(",", grant.Types.Select(t => t.Name)));
        return AgentResult<bool>.Ok(true);
    }

    /// <summary>Revokes (deletes) a grant: the agent's next call is refused.</summary>
    public async Task<bool> RevokeAsync(string userId, string? key, CancellationToken cancellationToken)
    {
        if (AgentGrantIds.FromKey(userId, key) is not { } id || await grants.GetOwnAsync(userId, id, cancellationToken) is not { } grant
            || !await grants.DeleteAsync(userId, id, cancellationToken))
            return false;

        LogGrantRevoked(logger, userId, id);

        // D12: an embedded-server grant is linked with its OAuth grant; revoking one revokes the other, so the agent's
        // refresh token stops working too, not only its next call.
        foreach (var link in services.GetServices<IAgentGrantLink>())
            await link.RevokedAsync(grant, cancellationToken);
        return true;
    }

    /// <summary>Revokes a contributed entry through its source.</summary>
    public async Task<bool> RevokeApplicationAsync(string userId, string? source, string? id, CancellationToken cancellationToken)
    {
        var owner = sources.FirstOrDefault(s => string.Equals(s.Name, source, StringComparison.Ordinal));
        return owner is not null && !string.IsNullOrEmpty(id) && await owner.RevokeAsync(userId, id, cancellationToken);
    }

    /// <summary>The refusal code, or <see langword="null"/> when the choice is within what may be granted.</summary>
    private static async Task<string?> ValidateAsync(
        IAgentExposure exposure, EAgentPermission permissions, List<AgentGrantType>? types, EAgentPermission max, CancellationToken cancellationToken)
    {
        if ((permissions & ~(EAgentPermission.Read | EAgentPermission.Write)) != 0 || !permissions.HasFlag(EAgentPermission.Read))
            return "agent_permission_not_offered";
        if ((permissions & max) != permissions)
            return "agent_permission_not_offered";
        if (types is null)
            return null;
        if (types.Count == 0)
            return "agent_no_types";

        var exposed = (await exposure.ListTypesAsync(cancellationToken)).ToDictionary(t => t.Name, StringComparer.Ordinal);
        foreach (var type in types)
        {
            if (!exposed.TryGetValue(type.Name ?? string.Empty, out var offered))
                return "agent_unknown_type";
            if (!type.Permissions.HasFlag(EAgentPermission.Read)
                || (type.Permissions & ~(EAgentPermission.Read | EAgentPermission.Write)) != 0
                || (type.Permissions & offered.Permissions & permissions) != type.Permissions)
            {
                return "agent_permission_not_offered";
            }
        }
        return null;
    }

    private static List<AgentGrantType>? Normalize(List<AgentGrantType>? types)
        => types?.GroupBy(t => t.Name, StringComparer.Ordinal).Select(g => new AgentGrantType { Name = g.Key, Permissions = g.First().Permissions }).ToList();

    private static AgentInformation TokenAgent(ClaimsPrincipal principal, string? tokenId, string? name)
        => new(EAgentKind.PersonalAccessToken, tokenId, null, name, principal);

    private static AgentInformation AgentOf(AgentGrant grant, ClaimsPrincipal principal)
        => new(grant.Kind, grant.ClientId, grant.VerifiedHost, grant.Name, principal) { GrantId = grant.Id };

    private static string StatusOf(AgentGrant grant, Identity.SparkUser? user, DateTimeOffset now)
    {
        if (grant.ExpiresAt is { } expires && expires <= now)
            return "expired";
        if (grant.Kind == EAgentKind.PersonalAccessToken
            && !string.Equals(user?.SecurityStamp ?? string.Empty, grant.SecurityStamp ?? string.Empty, StringComparison.Ordinal))
        {
            return "revoked";
        }
        return "active";
    }

    [LoggerMessage(EventId = 7320, EventName = "SparkPersonalAccessTokenCreated", Level = LogLevel.Information,
        Message = "User {UserId} created personal access token {GrantId}, expiring {ExpiresAt:O}.")]
    private static partial void LogTokenCreated(ILogger logger, string userId, string grantId, DateTimeOffset expiresAt);

    [LoggerMessage(EventId = 7321, EventName = "SparkAgentGrantChanged", Level = LogLevel.Information,
        Message = "User {UserId} changed agent grant {GrantId}: {Permissions}, types {Types}.")]
    private static partial void LogGrantChanged(ILogger logger, string userId, string grantId, string permissions, string types);

    [LoggerMessage(EventId = 7322, EventName = "SparkAgentGrantRevoked", Level = LogLevel.Information,
        Message = "User {UserId} revoked agent grant {GrantId}.")]
    private static partial void LogGrantRevoked(ILogger logger, string userId, string grantId);
}

/// <summary>An answer of <see cref="AgentGrantsAccount"/>: a value, a refusal code, or not found.</summary>
internal sealed record AgentResult<T>(T? Value, string? Error, bool IsNotFound)
{
    public static AgentResult<T> Ok(T value) => new(value, null, false);
    public static AgentResult<T> Fail(string error) => new(default, error, false);
    public static AgentResult<T> NotFound() => new(default, null, true);

    public IResult ToHttp() => IsNotFound
        ? Results.NotFound()
        : Error is not null ? Results.BadRequest(new { error = Error }) : Results.Ok(Value);
}

/// <summary><c>GET /spark/auth/manage/agents</c>.</summary>
/// <param name="PersonalAccessTokens">Whether tokens can be created (the Mcp package accepts them).</param>
/// <param name="DefaultTokenLifetimeDays">A new token's lifetime when none is picked.</param>
/// <param name="MaxTokenLifetimeDays">The longest lifetime a token may have.</param>
/// <param name="TokenPermissions">The most a new token may be granted (the app layer for personal access tokens).</param>
/// <param name="Types">The types the user may grant, each with its own most.</param>
/// <param name="Grants">The user's agent grants.</param>
/// <param name="Applications">Entries other packages contributed.</param>
internal sealed record AgentGrantsOverview(
    bool PersonalAccessTokens,
    int DefaultTokenLifetimeDays,
    int MaxTokenLifetimeDays,
    EAgentPermission TokenPermissions,
    IReadOnlyList<AgentExposedType> Types,
    IReadOnlyList<AgentGrantView> Grants,
    IReadOnlyList<ConnectedApplicationView> Applications);

/// <summary>One agent grant as the account page shows it. Never the token or its hash.</summary>
internal sealed record AgentGrantView(
    string Key,
    string Kind,
    string? Name,
    string? ClientId,
    string? VerifiedHost,
    EAgentPermission Permissions,
    IReadOnlyList<AgentGrantType>? Types,
    EAgentPermission MaxPermissions,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    string Status);

/// <summary>One contributed entry, with the source to revoke it through.</summary>
internal sealed record ConnectedApplicationView(string Source, string Id, string Name, string? Description, DateTimeOffset? CreatedAt, DateTimeOffset? LastUsedAt);

/// <summary>The one answer that ever carries a token.</summary>
internal sealed record AgentTokenCreated(string Token, string Key, DateTimeOffset ExpiresAt);

/// <summary><c>POST /spark/auth/manage/agents/tokens</c>.</summary>
public sealed class AgentTokenRequest
{
    /// <summary>What the user calls the token ("CI job"), 1 to 100 characters.</summary>
    public string? Name { get; set; }

    /// <summary>Days until it expires, 1 to 365; 90 when omitted.</summary>
    public int? ExpiresInDays { get; set; }

    /// <summary><c>Read</c>, or <c>Read, Write</c>.</summary>
    public EAgentPermission Permissions { get; set; } = EAgentPermission.Read;

    /// <summary>Omitted: everything the application makes available to agents. Otherwise the chosen types.</summary>
    public List<AgentGrantType>? Types { get; set; }
}

/// <summary><c>POST /spark/auth/manage/agents/update</c>.</summary>
public sealed class AgentGrantUpdateRequest
{
    /// <summary>The grant's key, as the overview lists it.</summary>
    public string? Key { get; set; }

    public EAgentPermission Permissions { get; set; } = EAgentPermission.Read;

    public List<AgentGrantType>? Types { get; set; }
}

/// <summary><c>POST /spark/auth/manage/agents/revoke</c>.</summary>
public sealed class AgentGrantRevokeRequest
{
    public string? Key { get; set; }
}

/// <summary><c>POST /spark/auth/manage/agents/applications/revoke</c>.</summary>
public sealed class ConnectedApplicationRevokeRequest
{
    public string? Source { get; set; }

    public string? Id { get; set; }
}
