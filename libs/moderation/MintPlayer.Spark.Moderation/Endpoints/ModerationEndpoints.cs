using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Requests;
using MintPlayer.Spark.Endpoints;
using MintPlayer.Spark.Moderation.Services;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Moderation.Endpoints;

/// <summary>
/// <c>/spark/moderation/*</c> (#460, T6). Literal routes, ids in the POST body (T1), antiforgery on
/// every one, answers in the standard envelope: a target the caller cannot see — hidden, missing, or
/// of a type that is not moderatable — is the standard refusal (401 anonymous / 404), so the vote and
/// flag endpoints never reveal that a hidden post exists (#453). A rule is 400, a quota 429.
/// </summary>
internal class ModerationGroup : IEndpointGroup
{
    public static string Prefix => "/spark/moderation";
}

/// <summary>A typed request naming one target.</summary>
internal sealed class ModerationTargetRequest : ISparkTypedRequest
{
    public string? ObjectTypeId { get; set; }
    public string? Id { get; set; }
    public int Direction { get; set; }
    public string? Reason { get; set; }
}

/// <summary>A typed request naming several targets of one type.</summary>
internal sealed class ModerationTargetsRequest : ISparkTypedRequest
{
    public string? ObjectTypeId { get; set; }
    public List<string>? Ids { get; set; }
}

/// <summary>An untyped request (accounts, cases, pages).</summary>
internal sealed class ModerationRequest
{
    public string? UserId { get; set; }
    public string? IntoUserId { get; set; }
    public int? Days { get; set; }
    public string? Reason { get; set; }
    public string? CaseId { get; set; }
    public string? Decision { get; set; }
    public string? AccountId { get; set; }
    public string? Status { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 50;
}

internal static class ModerationEndpoint
{
    public static void Configure(RouteHandlerBuilder builder) => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    /// <summary>Runs <paramref name="operation"/> and maps Spark's refusals onto the envelope.</summary>
    public static async Task<IResult> RunAsync(HttpContext httpContext, IClientAccessor client, Func<Task<object?>> operation, int status = StatusCodes.Status200OK)
    {
        try
        {
            var result = await operation();
            return SparkAddOnEndpoints.Envelope(client, result, status);
        }
        catch (SparkValidationException ex)
        {
            return SparkAddOnEndpoints.ValidationFailed(client, ex);
        }
        catch (SparkThrottledException ex)
        {
            return SparkAddOnEndpoints.Throttled(client, httpContext, ex);
        }
        catch (SparkAccessDeniedException)
        {
            return SparkAddOnEndpoints.Refusal(client, httpContext);
        }
    }

    public static async Task<IResult> TypedAsync<TRequest>(HttpContext httpContext, IModelLoader modelLoader, IClientAccessor client, Func<TRequest, Guid, Task<object?>> operation)
        where TRequest : class, ISparkTypedRequest
    {
        var (request, entityType) = await SparkAddOnEndpoints.ReadTypedRequestAsync<TRequest>(httpContext, modelLoader);
        if (request is null || entityType is null)
            return SparkAddOnEndpoints.Refusal(client, httpContext);
        return await RunAsync(httpContext, client, () => operation(request, entityType.Id));
    }

    public static async Task<IResult> UntypedAsync(HttpContext httpContext, IClientAccessor client, ISparkCurrentUser user, Func<ModerationRequest, Task<object?>> operation)
    {
        if (!user.IsAuthenticated)
            return SparkAddOnEndpoints.Refusal(client, httpContext);
        ModerationRequest? request;
        try
        {
            request = httpContext.Request.ContentLength is 0 ? new ModerationRequest() : await httpContext.Request.ReadFromJsonAsync<ModerationRequest>();
        }
        catch (Exception ex) when (ex is JsonException or BadHttpRequestException)
        {
            request = null;
        }
        if (request is null)
            return SparkAddOnEndpoints.Refusal(client, httpContext);
        return await RunAsync(httpContext, client, () => operation(request));
    }
}

/// <summary><c>POST /spark/moderation/vote { objectTypeId, id, direction }</c> → the target's vote state.</summary>
[MemberOf<ModerationGroup>]
internal sealed partial class VoteEndpoint : IPostEndpoint
{
    public static string Path => "/vote";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder) => ModerationEndpoint.Configure(builder);
    [Inject] private readonly ISparkModeration moderation;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IClientAccessor clientAccessor;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => ModerationEndpoint.TypedAsync<ModerationTargetRequest>(httpContext, modelLoader, clientAccessor,
            async (r, type) => await moderation.VoteAsync(type, r.Id ?? string.Empty, r.Direction, httpContext.RequestAborted));
}

/// <summary><c>POST /spark/moderation/votes { objectTypeId, ids }</c> → vote states of the visible ids (≤ 100).</summary>
[MemberOf<ModerationGroup>]
internal sealed partial class VotesEndpoint : IPostEndpoint
{
    public static string Path => "/votes";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder) => ModerationEndpoint.Configure(builder);
    [Inject] private readonly ISparkModeration moderation;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IClientAccessor clientAccessor;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => ModerationEndpoint.TypedAsync<ModerationTargetsRequest>(httpContext, modelLoader, clientAccessor,
            async (r, type) => await moderation.GetVotesAsync(type, r.Ids ?? [], httpContext.RequestAborted));
}

/// <summary><c>POST /spark/moderation/flag { objectTypeId, id, reason }</c> → 204.</summary>
[MemberOf<ModerationGroup>]
internal sealed partial class FlagEndpoint : IPostEndpoint
{
    public static string Path => "/flag";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder) => ModerationEndpoint.Configure(builder);
    [Inject] private readonly ISparkModeration moderation;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IClientAccessor clientAccessor;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => ModerationEndpoint.TypedAsync<ModerationTargetRequest>(httpContext, modelLoader, clientAccessor,
            async (r, type) => { await moderation.FlagAsync(type, r.Id ?? string.Empty, r.Reason ?? string.Empty, httpContext.RequestAborted); return null; });
}

/// <summary><c>POST /spark/moderation/lock { objectTypeId, id, reason }</c>. Requires <c>Lock/T</c>.</summary>
[MemberOf<ModerationGroup>]
internal sealed partial class LockEndpoint : IPostEndpoint
{
    public static string Path => "/lock";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder) => ModerationEndpoint.Configure(builder);
    [Inject] private readonly ISparkModeration moderation;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IClientAccessor clientAccessor;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => ModerationEndpoint.TypedAsync<ModerationTargetRequest>(httpContext, modelLoader, clientAccessor,
            async (r, type) => { await moderation.LockAsync(type, r.Id ?? string.Empty, r.Reason, httpContext.RequestAborted); return null; });
}

/// <summary><c>POST /spark/moderation/unlock { objectTypeId, id }</c>. Requires <c>Lock/T</c>.</summary>
[MemberOf<ModerationGroup>]
internal sealed partial class UnlockEndpoint : IPostEndpoint
{
    public static string Path => "/unlock";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder) => ModerationEndpoint.Configure(builder);
    [Inject] private readonly ISparkModeration moderation;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IClientAccessor clientAccessor;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => ModerationEndpoint.TypedAsync<ModerationTargetRequest>(httpContext, modelLoader, clientAccessor,
            async (r, type) => { await moderation.UnlockAsync(type, r.Id ?? string.Empty, httpContext.RequestAborted); return null; });
}

/// <summary>
/// <c>POST /spark/moderation/status { objectTypeId, id }</c> → what the moderator panel shows: lock,
/// open flag case, and which moderator actions the caller holds.
/// </summary>
[MemberOf<ModerationGroup>]
internal sealed partial class StatusEndpoint : IPostEndpoint
{
    public static string Path => "/status";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder) => ModerationEndpoint.Configure(builder);
    [Inject] private readonly ModerationTargets targets;
    [Inject] private readonly IPermissionService permissions;
    [Inject] private readonly Raven.Client.Documents.IDocumentStore documentStore;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IClientAccessor clientAccessor;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => ModerationEndpoint.TypedAsync<ModerationTargetRequest>(httpContext, modelLoader, clientAccessor, async (r, type) =>
        {
            var target = await targets.ResolveAsync(type, r.Id ?? string.Empty);
            var canLock = await permissions.IsAllowedAsync(ModerationRights.Lock, target.TypeName);
            var canReview = await permissions.IsAllowedAsync(ModerationRights.Review, ModerationRights.Target);
            using var session = documentStore.OpenAsyncSession();
            var lockDoc = await session.LoadAsync<Documents.ModerationLock>(Documents.ModerationIds.Lock(target.Id));
            var flagCase = canReview ? await session.LoadAsync<Documents.ReviewCase>(Documents.ModerationIds.FlagCase(target.Id)) : null;
            return new
            {
                id = target.Id,
                authorId = target.AuthorId,
                locked = lockDoc is not null,
                lockReason = canLock ? lockDoc?.Reason : null,
                canLock,
                canReview,
                canSuspend = await permissions.IsAllowedAsync(ModerationRights.Suspend, ModerationRights.Target),
                openCaseId = flagCase?.Status == Documents.ModerationCaseStatus.Open ? flagCase.Id : null,
                openFlags = flagCase?.Status == Documents.ModerationCaseStatus.Open ? flagCase.FlagCount : 0,
            };
        });
}

/// <summary>
/// <c>POST /spark/moderation/reputation { userId? }</c> → a user's reputation (the caller's without
/// one); for an anonymous caller a 200 with no result.
/// </summary>
/// <remarks>
/// ⚠️ Anonymous is answered, not refused: the reputation badge is a background widget on pages an
/// anonymous visitor may read (every author cell of a question list), and ng-spark-auth's interceptor
/// sends the whole app to the sign-in page on any 401 outside <c>/spark/auth</c>. An anonymous caller
/// has no reputation of its own and is shown nobody else's, so the honest answer is "none" — the
/// badge stays empty. Nothing is disclosed that the 401 withheld.
/// </remarks>
[MemberOf<ModerationGroup>]
internal sealed partial class ReputationEndpoint : IPostEndpoint
{
    public static string Path => "/reputation";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder) => ModerationEndpoint.Configure(builder);
    [Inject] private readonly ISparkModeration moderation;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly IClientAccessor clientAccessor;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => !currentUser.IsAuthenticated
            ? Task.FromResult(SparkAddOnEndpoints.Envelope(clientAccessor, null, StatusCodes.Status200OK))
            : ModerationEndpoint.UntypedAsync(httpContext, clientAccessor, currentUser, async r =>
        {
            var userId = string.IsNullOrEmpty(r.UserId) ? currentUser.Id! : r.UserId;
            var reputation = await moderation.GetReputationAsync(userId, httpContext.RequestAborted);
            // Someone else's badge shows the number only; privileges and suspension are the owner's business.
            return userId == currentUser.Id ? reputation : new ModerationReputation { UserId = userId, Total = reputation.Total };
        });
}

/// <summary><c>POST /spark/moderation/reputation/history</c> → the caller's own ledger, voters never named.</summary>
[MemberOf<ModerationGroup>]
internal sealed partial class ReputationHistoryEndpoint : IPostEndpoint
{
    public static string Path => "/reputation/history";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder) => ModerationEndpoint.Configure(builder);
    [Inject] private readonly ModerationReview review;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly IClientAccessor clientAccessor;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => ModerationEndpoint.UntypedAsync(httpContext, clientAccessor, currentUser,
            async r => await review.MyHistoryAsync(r.Take, httpContext.RequestAborted));
}

/// <summary><c>POST /spark/moderation/cases { status?, skip, take }</c> → the review queue. Requires <c>Review/Moderation</c>.</summary>
[MemberOf<ModerationGroup>]
internal sealed partial class CasesEndpoint : IPostEndpoint
{
    public static string Path => "/cases";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder) => ModerationEndpoint.Configure(builder);
    [Inject] private readonly ModerationReview review;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly IClientAccessor clientAccessor;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => ModerationEndpoint.UntypedAsync(httpContext, clientAccessor, currentUser,
            async r => await review.ListCasesAsync(r.Status, r.Skip, r.Take, httpContext.RequestAborted));
}

/// <summary><c>POST /spark/moderation/case { caseId }</c> → the case's review surface. Requires <c>Review/Moderation</c>.</summary>
[MemberOf<ModerationGroup>]
internal sealed partial class CaseEndpoint : IPostEndpoint
{
    public static string Path => "/case";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder) => ModerationEndpoint.Configure(builder);
    [Inject] private readonly ModerationReview review;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly IClientAccessor clientAccessor;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => ModerationEndpoint.UntypedAsync(httpContext, clientAccessor, currentUser,
            async r => await review.GetCaseAsync(r.CaseId ?? string.Empty, httpContext.RequestAborted));
}

/// <summary><c>POST /spark/moderation/case/decide { caseId, decision, accountId?, reason? }</c> → 204.</summary>
[MemberOf<ModerationGroup>]
internal sealed partial class DecideEndpoint : IPostEndpoint
{
    public static string Path => "/case/decide";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder) => ModerationEndpoint.Configure(builder);
    [Inject] private readonly ISparkModeration moderation;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly IClientAccessor clientAccessor;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => ModerationEndpoint.UntypedAsync(httpContext, clientAccessor, currentUser, async r =>
        {
            await moderation.DecideAsync(r.CaseId ?? string.Empty, r.Decision ?? string.Empty, r.AccountId, r.Reason, httpContext.RequestAborted);
            return null;
        });
}

/// <summary><c>POST /spark/moderation/suspend { userId, days?, reason? }</c>. Requires <c>Suspend/Moderation</c>.</summary>
[MemberOf<ModerationGroup>]
internal sealed partial class SuspendEndpoint : IPostEndpoint
{
    public static string Path => "/suspend";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder) => ModerationEndpoint.Configure(builder);
    [Inject] private readonly ISparkModeration moderation;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly IClientAccessor clientAccessor;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => ModerationEndpoint.UntypedAsync(httpContext, clientAccessor, currentUser, async r =>
        {
            await moderation.SuspendAsync(r.UserId ?? string.Empty, r.Days, r.Reason, r.CaseId, httpContext.RequestAborted);
            return null;
        });
}

/// <summary><c>POST /spark/moderation/unsuspend { userId }</c>. Requires <c>Suspend/Moderation</c>.</summary>
[MemberOf<ModerationGroup>]
internal sealed partial class UnsuspendEndpoint : IPostEndpoint
{
    public static string Path => "/unsuspend";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder) => ModerationEndpoint.Configure(builder);
    [Inject] private readonly ISparkModeration moderation;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly IClientAccessor clientAccessor;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => ModerationEndpoint.UntypedAsync(httpContext, clientAccessor, currentUser, async r =>
        {
            await moderation.UnsuspendAsync(r.UserId ?? string.Empty, httpContext.RequestAborted);
            return null;
        });
}

/// <summary><c>POST /spark/moderation/merge { userId, intoUserId }</c>: reverses every vote the duplicate cast. Requires <c>Suspend/Moderation</c>.</summary>
[MemberOf<ModerationGroup>]
internal sealed partial class MergeEndpoint : IPostEndpoint
{
    public static string Path => "/merge";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder) => ModerationEndpoint.Configure(builder);
    [Inject] private readonly ISparkModeration moderation;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly IClientAccessor clientAccessor;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => ModerationEndpoint.UntypedAsync(httpContext, clientAccessor, currentUser, async r =>
        {
            await moderation.MergeAccountsAsync(r.UserId ?? string.Empty, r.IntoUserId ?? string.Empty, r.CaseId, httpContext.RequestAborted);
            return null;
        });
}

/// <summary><c>POST /spark/moderation/audit { skip, take }</c> → the audit log, newest first. Requires <c>Audit/Moderation</c>.</summary>
[MemberOf<ModerationGroup>]
internal sealed partial class AuditEndpoint : IPostEndpoint
{
    public static string Path => "/audit";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder) => ModerationEndpoint.Configure(builder);
    [Inject] private readonly ModerationReview review;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly IClientAccessor clientAccessor;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => ModerationEndpoint.UntypedAsync(httpContext, clientAccessor, currentUser,
            async r => await review.ListAuditAsync(r.Skip, r.Take, httpContext.RequestAborted));
}
