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
    public static async Task<IResult> RunAsync(HttpContext httpContext, ISparkAddOnEndpoints addOn, Func<Task<object?>> operation, int status = StatusCodes.Status200OK)
    {
        try
        {
            var result = await operation();
            return addOn.Envelope(result, status);
        }
        catch (SparkValidationException ex)
        {
            return addOn.ValidationFailed(ex);
        }
        catch (SparkThrottledException ex)
        {
            return addOn.Throttled(httpContext, ex);
        }
        catch (SparkAccessDeniedException)
        {
            return addOn.Refusal(httpContext);
        }
    }

    /// <summary>
    /// A typed request: the type it names (top-level <c>objectTypeId</c>), or the standard refusal when
    /// it names none the model declares.
    /// </summary>
    public static Task<IResult> TypedAsync(HttpContext httpContext, ISparkAddOnEndpoints addOn, ISparkTypedRequest request, Func<Guid, Task<object?>> operation)
        => addOn.ResolveType(request) is { } entityType
            ? RunAsync(httpContext, addOn, () => operation(entityType.Id))
            : Task.FromResult(addOn.Refusal(httpContext));

    /// <summary>An untyped request (accounts, cases, pages): signed-in callers only.</summary>
    public static Task<IResult> UntypedAsync(HttpContext httpContext, ISparkAddOnEndpoints addOn, ISparkCurrentUser user, Func<Task<object?>> operation)
        => user.IsAuthenticated
            ? RunAsync(httpContext, addOn, operation)
            : Task.FromResult(addOn.Refusal(httpContext));

    /// <summary>
    /// The binding of an untyped request: a body-less one (<c>Content-Length: 0</c>) is an empty request,
    /// as it was while these endpoints read their bodies by hand; anything else is bound as JSON.
    /// </summary>
    public static ValueTask<ModerationRequest?> BindAsync(HttpContext context, Func<HttpContext, ValueTask<ModerationRequest?>> bindBody)
        => context.Request.ContentLength is 0 ? new(new ModerationRequest()) : bindBody(context);
}

// Every endpoint below is typed (endpoints generator completion M3). A body that cannot be bound is
// the standard refusal, never a parse error: measured before the change, a malformed or JSON-null body
// was that refusal already, while a request with no content type escaped as an unhandled
// InvalidOperationException (a 500) and is now the refusal too (ModerationToolsTests.Unbindable_bodies_answer_as_before).

/// <summary><c>POST /spark/moderation/vote { objectTypeId, id, direction }</c> → the target's vote state.</summary>
[MemberOf<ModerationGroup>]
internal sealed partial class VoteEndpoint : IPostEndpoint<ModerationTargetRequest>
{
    public static string Path => "/vote";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => ModerationEndpoint.Configure(builder);
    [Inject] private readonly ISparkModeration moderation;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure) => new(addOn.Refusal(context));

    public override Task<IResult> HandleAsync(ModerationTargetRequest r, CancellationToken cancellationToken)
        => ModerationEndpoint.TypedAsync(httpContextAccessor.HttpContext!, addOn, r,
            async type => await moderation.VoteAsync(type, r.Id ?? string.Empty, r.Direction, cancellationToken));
}

/// <summary><c>POST /spark/moderation/votes { objectTypeId, ids }</c> → vote states of the visible ids (≤ 100).</summary>
[MemberOf<ModerationGroup>]
internal sealed partial class VotesEndpoint : IPostEndpoint<ModerationTargetsRequest>
{
    public static string Path => "/votes";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => ModerationEndpoint.Configure(builder);
    [Inject] private readonly ISparkModeration moderation;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure) => new(addOn.Refusal(context));

    public override Task<IResult> HandleAsync(ModerationTargetsRequest r, CancellationToken cancellationToken)
        => ModerationEndpoint.TypedAsync(httpContextAccessor.HttpContext!, addOn, r,
            async type => await moderation.GetVotesAsync(type, r.Ids ?? [], cancellationToken));
}

/// <summary><c>POST /spark/moderation/flag { objectTypeId, id, reason }</c> → 204.</summary>
[MemberOf<ModerationGroup>]
internal sealed partial class FlagEndpoint : IPostEndpoint<ModerationTargetRequest>
{
    public static string Path => "/flag";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => ModerationEndpoint.Configure(builder);
    [Inject] private readonly ISparkModeration moderation;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure) => new(addOn.Refusal(context));

    public override Task<IResult> HandleAsync(ModerationTargetRequest r, CancellationToken cancellationToken)
        => ModerationEndpoint.TypedAsync(httpContextAccessor.HttpContext!, addOn, r,
            async type => { await moderation.FlagAsync(type, r.Id ?? string.Empty, r.Reason ?? string.Empty, cancellationToken); return null; });
}

/// <summary><c>POST /spark/moderation/lock { objectTypeId, id, reason }</c>. Requires <c>Lock/T</c>.</summary>
[MemberOf<ModerationGroup>]
internal sealed partial class LockEndpoint : IPostEndpoint<ModerationTargetRequest>
{
    public static string Path => "/lock";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => ModerationEndpoint.Configure(builder);
    [Inject] private readonly ISparkModeration moderation;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure) => new(addOn.Refusal(context));

    public override Task<IResult> HandleAsync(ModerationTargetRequest r, CancellationToken cancellationToken)
        => ModerationEndpoint.TypedAsync(httpContextAccessor.HttpContext!, addOn, r,
            async type => { await moderation.LockAsync(type, r.Id ?? string.Empty, r.Reason, cancellationToken); return null; });
}

/// <summary><c>POST /spark/moderation/unlock { objectTypeId, id }</c>. Requires <c>Lock/T</c>.</summary>
[MemberOf<ModerationGroup>]
internal sealed partial class UnlockEndpoint : IPostEndpoint<ModerationTargetRequest>
{
    public static string Path => "/unlock";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => ModerationEndpoint.Configure(builder);
    [Inject] private readonly ISparkModeration moderation;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure) => new(addOn.Refusal(context));

    public override Task<IResult> HandleAsync(ModerationTargetRequest r, CancellationToken cancellationToken)
        => ModerationEndpoint.TypedAsync(httpContextAccessor.HttpContext!, addOn, r,
            async type => { await moderation.UnlockAsync(type, r.Id ?? string.Empty, cancellationToken); return null; });
}

/// <summary>
/// <c>POST /spark/moderation/status { objectTypeId, id }</c> → what the moderator panel shows: lock,
/// open flag case, and which moderator actions the caller holds.
/// </summary>
[MemberOf<ModerationGroup>]
internal sealed partial class StatusEndpoint : IPostEndpoint<ModerationTargetRequest>
{
    public static string Path => "/status";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => ModerationEndpoint.Configure(builder);
    [Inject] private readonly ModerationTargets targets;
    [Inject] private readonly IPermissionService permissions;
    [Inject] private readonly Raven.Client.Documents.IDocumentStore documentStore;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure) => new(addOn.Refusal(context));

    public override Task<IResult> HandleAsync(ModerationTargetRequest r, CancellationToken cancellationToken)
        => ModerationEndpoint.TypedAsync(httpContextAccessor.HttpContext!, addOn, r, async type =>
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
/// anonymous visitor may read (every author cell of a question list), and ng-spark/auth's interceptor
/// sends the whole app to the sign-in page on any 401 outside <c>/spark/auth</c>. An anonymous caller
/// has no reputation of its own and is shown nobody else's, so the honest answer is "none" — the
/// badge stays empty. Nothing is disclosed that the 401 withheld. That holds for a body that cannot be
/// bound too, as it did before the endpoint was typed.
/// </remarks>
[MemberOf<ModerationGroup>]
internal sealed partial class ReputationEndpoint : IPostEndpoint<ModerationRequest>
{
    public static string Path => "/reputation";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => ModerationEndpoint.Configure(builder);
    [Inject] private readonly ISparkModeration moderation;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    private HttpContext httpContext = null!;

    protected override ValueTask<ModerationRequest?> BindRequestAsync(HttpContext context)
        => ModerationEndpoint.BindAsync(httpContext = context, base.BindRequestAsync);

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => new(currentUser.IsAuthenticated ? addOn.Refusal(context) : addOn.Envelope(null, StatusCodes.Status200OK));

    public override Task<IResult> HandleAsync(ModerationRequest r, CancellationToken cancellationToken)
        => !currentUser.IsAuthenticated
            ? Task.FromResult(addOn.Envelope(null, StatusCodes.Status200OK))
            : ModerationEndpoint.UntypedAsync(httpContext, addOn, currentUser, async () =>
        {
            var userId = string.IsNullOrEmpty(r.UserId) ? currentUser.Id! : r.UserId;
            var reputation = await moderation.GetReputationAsync(userId, cancellationToken);
            // Someone else's badge shows the number only; privileges and suspension are the owner's business.
            return userId == currentUser.Id ? reputation : new ModerationReputation { UserId = userId, Total = reputation.Total };
        });
}

/// <summary><c>POST /spark/moderation/reputation/history</c> → the caller's own ledger, voters never named.</summary>
[MemberOf<ModerationGroup>]
internal sealed partial class ReputationHistoryEndpoint : IPostEndpoint<ModerationRequest>
{
    public static string Path => "/reputation/history";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => ModerationEndpoint.Configure(builder);
    [Inject] private readonly ModerationReview review;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    private HttpContext httpContext = null!;

    protected override ValueTask<ModerationRequest?> BindRequestAsync(HttpContext context)
        => ModerationEndpoint.BindAsync(httpContext = context, base.BindRequestAsync);

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure) => new(addOn.Refusal(context));

    public override Task<IResult> HandleAsync(ModerationRequest r, CancellationToken cancellationToken)
        => ModerationEndpoint.UntypedAsync(httpContext, addOn, currentUser,
            async () => await review.MyHistoryAsync(r.Take, cancellationToken));
}

/// <summary><c>POST /spark/moderation/cases { status?, skip, take }</c> → the review queue. Requires <c>Review/Moderation</c>.</summary>
[MemberOf<ModerationGroup>]
internal sealed partial class CasesEndpoint : IPostEndpoint<ModerationRequest>
{
    public static string Path => "/cases";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => ModerationEndpoint.Configure(builder);
    [Inject] private readonly ModerationReview review;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    private HttpContext httpContext = null!;

    protected override ValueTask<ModerationRequest?> BindRequestAsync(HttpContext context)
        => ModerationEndpoint.BindAsync(httpContext = context, base.BindRequestAsync);

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure) => new(addOn.Refusal(context));

    public override Task<IResult> HandleAsync(ModerationRequest r, CancellationToken cancellationToken)
        => ModerationEndpoint.UntypedAsync(httpContext, addOn, currentUser,
            async () => await review.ListCasesAsync(r.Status, r.Skip, r.Take, cancellationToken));
}

/// <summary><c>POST /spark/moderation/case { caseId }</c> → the case's review surface. Requires <c>Review/Moderation</c>.</summary>
[MemberOf<ModerationGroup>]
internal sealed partial class CaseEndpoint : IPostEndpoint<ModerationRequest>
{
    public static string Path => "/case";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => ModerationEndpoint.Configure(builder);
    [Inject] private readonly ModerationReview review;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    private HttpContext httpContext = null!;

    protected override ValueTask<ModerationRequest?> BindRequestAsync(HttpContext context)
        => ModerationEndpoint.BindAsync(httpContext = context, base.BindRequestAsync);

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure) => new(addOn.Refusal(context));

    public override Task<IResult> HandleAsync(ModerationRequest r, CancellationToken cancellationToken)
        => ModerationEndpoint.UntypedAsync(httpContext, addOn, currentUser,
            async () => await review.GetCaseAsync(r.CaseId ?? string.Empty, cancellationToken));
}

/// <summary><c>POST /spark/moderation/case/decide { caseId, decision, accountId?, reason? }</c> → 204.</summary>
[MemberOf<ModerationGroup>]
internal sealed partial class DecideEndpoint : IPostEndpoint<ModerationRequest>
{
    public static string Path => "/case/decide";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => ModerationEndpoint.Configure(builder);
    [Inject] private readonly ISparkModeration moderation;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    private HttpContext httpContext = null!;

    protected override ValueTask<ModerationRequest?> BindRequestAsync(HttpContext context)
        => ModerationEndpoint.BindAsync(httpContext = context, base.BindRequestAsync);

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure) => new(addOn.Refusal(context));

    public override Task<IResult> HandleAsync(ModerationRequest r, CancellationToken cancellationToken)
        => ModerationEndpoint.UntypedAsync(httpContext, addOn, currentUser, async () =>
        {
            await moderation.DecideAsync(r.CaseId ?? string.Empty, r.Decision ?? string.Empty, r.AccountId, r.Reason, cancellationToken);
            return null;
        });
}

/// <summary><c>POST /spark/moderation/suspend { userId, days?, reason? }</c>. Requires <c>Suspend/Moderation</c>.</summary>
[MemberOf<ModerationGroup>]
internal sealed partial class SuspendEndpoint : IPostEndpoint<ModerationRequest>
{
    public static string Path => "/suspend";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => ModerationEndpoint.Configure(builder);
    [Inject] private readonly ISparkModeration moderation;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    private HttpContext httpContext = null!;

    protected override ValueTask<ModerationRequest?> BindRequestAsync(HttpContext context)
        => ModerationEndpoint.BindAsync(httpContext = context, base.BindRequestAsync);

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure) => new(addOn.Refusal(context));

    public override Task<IResult> HandleAsync(ModerationRequest r, CancellationToken cancellationToken)
        => ModerationEndpoint.UntypedAsync(httpContext, addOn, currentUser, async () =>
        {
            await moderation.SuspendAsync(r.UserId ?? string.Empty, r.Days, r.Reason, r.CaseId, cancellationToken);
            return null;
        });
}

/// <summary><c>POST /spark/moderation/unsuspend { userId }</c>. Requires <c>Suspend/Moderation</c>.</summary>
[MemberOf<ModerationGroup>]
internal sealed partial class UnsuspendEndpoint : IPostEndpoint<ModerationRequest>
{
    public static string Path => "/unsuspend";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => ModerationEndpoint.Configure(builder);
    [Inject] private readonly ISparkModeration moderation;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    private HttpContext httpContext = null!;

    protected override ValueTask<ModerationRequest?> BindRequestAsync(HttpContext context)
        => ModerationEndpoint.BindAsync(httpContext = context, base.BindRequestAsync);

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure) => new(addOn.Refusal(context));

    public override Task<IResult> HandleAsync(ModerationRequest r, CancellationToken cancellationToken)
        => ModerationEndpoint.UntypedAsync(httpContext, addOn, currentUser, async () =>
        {
            await moderation.UnsuspendAsync(r.UserId ?? string.Empty, cancellationToken);
            return null;
        });
}

/// <summary><c>POST /spark/moderation/merge { userId, intoUserId }</c>: reverses every vote the duplicate cast. Requires <c>Suspend/Moderation</c>.</summary>
[MemberOf<ModerationGroup>]
internal sealed partial class MergeEndpoint : IPostEndpoint<ModerationRequest>
{
    public static string Path => "/merge";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => ModerationEndpoint.Configure(builder);
    [Inject] private readonly ISparkModeration moderation;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    private HttpContext httpContext = null!;

    protected override ValueTask<ModerationRequest?> BindRequestAsync(HttpContext context)
        => ModerationEndpoint.BindAsync(httpContext = context, base.BindRequestAsync);

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure) => new(addOn.Refusal(context));

    public override Task<IResult> HandleAsync(ModerationRequest r, CancellationToken cancellationToken)
        => ModerationEndpoint.UntypedAsync(httpContext, addOn, currentUser, async () =>
        {
            await moderation.MergeAccountsAsync(r.UserId ?? string.Empty, r.IntoUserId ?? string.Empty, r.CaseId, cancellationToken);
            return null;
        });
}

/// <summary><c>POST /spark/moderation/audit { skip, take }</c> → the audit log, newest first. Requires <c>Audit/Moderation</c>.</summary>
[MemberOf<ModerationGroup>]
internal sealed partial class AuditEndpoint : IPostEndpoint<ModerationRequest>
{
    public static string Path => "/audit";
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => ModerationEndpoint.Configure(builder);
    [Inject] private readonly ModerationReview review;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    private HttpContext httpContext = null!;

    protected override ValueTask<ModerationRequest?> BindRequestAsync(HttpContext context)
        => ModerationEndpoint.BindAsync(httpContext = context, base.BindRequestAsync);

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure) => new(addOn.Refusal(context));

    public override Task<IResult> HandleAsync(ModerationRequest r, CancellationToken cancellationToken)
        => ModerationEndpoint.UntypedAsync(httpContext, addOn, currentUser,
            async () => await review.ListAuditAsync(r.Skip, r.Take, cancellationToken));
}
