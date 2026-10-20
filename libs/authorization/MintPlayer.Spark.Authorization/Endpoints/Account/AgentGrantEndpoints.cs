using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.Spark.Authorization.Contracts;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Authorization.Agents;

namespace MintPlayer.Spark.Authorization.Endpoints.Account;

/// <summary>
/// <c>GET /spark/auth/manage/agents</c>: the signed-in user's <b>Connected applications</b> (MCP PRD D14) — agent
/// grants, personal access tokens and the entries other packages contribute — and what may be granted. Mapped
/// when there is something to show: the Mcp package, or a package contributing entries.
/// </summary>
/// <remarks>
/// Like every <c>/manage</c> endpoint it answers the web's credentials (the cookie), never an agent token: a
/// personal access token is not one of Spark's web credentials, so an agent cannot read or widen its own grant.
/// </remarks>
[MemberOf<SparkAuthManageGroup>]
internal sealed partial class ListAgentGrants : IGetEndpoint
{
    public static string Path => "/agents";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => AgentGrantsAccount.IsAvailable(services);

    [Inject] private readonly AgentGrantsAccount account;
    [Inject] private readonly ISparkCurrentUser currentUser;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (currentUser.Id is not { Length: > 0 } userId)
            return Results.NotFound();

        httpContext.Response.Headers.CacheControl = "no-store";
        return Results.Ok(await account.OverviewAsync(httpContext.User, userId, httpContext.RequestAborted));
    }
}

/// <summary>
/// <c>POST /spark/auth/manage/agents/tokens</c>: creates a personal access token (D11) with its grant. The answer
/// is the only place the token ever appears; it is stored hashed and never logged.
/// </summary>
[MemberOf<SparkAuthManageGroup>]
internal sealed partial class CreateAgentToken : IPostEndpoint<AgentTokenRequest>
{
    public static string Path => "/agents/tokens";

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => SparkAccount.BindFailed(context, failure);

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => AgentGrantsAccount.IsAvailable(services);

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly AgentGrantsAccount account;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(AgentTokenRequest request, CancellationToken cancellationToken)
    {
        if (currentUser.Id is not { Length: > 0 } userId)
            return Results.NotFound();

        var context = httpContextAccessor.HttpContext!;
        context.Response.Headers.CacheControl = "no-store";
        return (await account.CreateTokenAsync(context.User, userId, request, cancellationToken)).ToHttp();
    }
}

/// <summary><c>POST /spark/auth/manage/agents/update</c>: changes a grant's types and read/changes; the agent's next call uses them.</summary>
[MemberOf<SparkAuthManageGroup>]
internal sealed partial class UpdateAgentGrant : IPostEndpoint<AgentGrantUpdateRequest>
{
    public static string Path => "/agents/update";

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => SparkAccount.BindFailed(context, failure);

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => AgentGrantsAccount.IsAvailable(services);

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly AgentGrantsAccount account;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(AgentGrantUpdateRequest request, CancellationToken cancellationToken)
    {
        if (currentUser.Id is not { Length: > 0 } userId)
            return Results.NotFound();

        return (await account.UpdateAsync(httpContextAccessor.HttpContext!.User, userId, request, cancellationToken)).ToHttp();
    }
}

/// <summary><c>POST /spark/auth/manage/agents/revoke</c>: deletes a grant (disconnects the agent, revokes the token).</summary>
[MemberOf<SparkAuthManageGroup>]
internal sealed partial class RevokeAgentGrant : IPostEndpoint<AgentGrantRevokeRequest>
{
    public static string Path => "/agents/revoke";

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => SparkAccount.BindFailed(context, failure);

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => AgentGrantsAccount.IsAvailable(services);

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly AgentGrantsAccount account;
    [Inject] private readonly ISparkCurrentUser currentUser;

    public override async Task<IResult> HandleAsync(AgentGrantRevokeRequest request, CancellationToken cancellationToken)
    {
        if (currentUser.Id is not { Length: > 0 } userId)
            return Results.NotFound();

        return await account.RevokeAsync(userId, request.Key, cancellationToken)
            ? Results.Ok(SparkGrantRevoked.Done)
            : Results.NotFound();
    }
}

/// <summary><c>POST /spark/auth/manage/agents/applications/revoke</c>: revokes an entry another package contributed.</summary>
[MemberOf<SparkAuthManageGroup>]
internal sealed partial class RevokeConnectedApplication : IPostEndpoint<ConnectedApplicationRevokeRequest>
{
    public static string Path => "/agents/applications/revoke";

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => SparkAccount.BindFailed(context, failure);

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => AgentGrantsAccount.IsAvailable(services);

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly AgentGrantsAccount account;
    [Inject] private readonly ISparkCurrentUser currentUser;

    public override async Task<IResult> HandleAsync(ConnectedApplicationRevokeRequest request, CancellationToken cancellationToken)
    {
        if (currentUser.Id is not { Length: > 0 } userId)
            return Results.NotFound();

        return await account.RevokeApplicationAsync(userId, request.Source, request.Id, cancellationToken)
            ? Results.Ok(SparkGrantRevoked.Done)
            : Results.NotFound();
    }
}
