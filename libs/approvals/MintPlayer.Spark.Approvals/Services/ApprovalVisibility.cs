using System.Linq.Expressions;
using System.Security.Claims;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Approvals.Models;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Approvals.Services;

/// <summary>
/// Who sees which request, and who may decide it. A request is visible to its requester, its listed deciders,
/// <see cref="ApprovalRequest.VisibleTo"/>, and a kind's unlisted deciders (<see cref="IApprovalDeciders{TKind}.DecidesAllAsync"/>).
/// Deciding asks the kind's live gate, and never lets the requester decide their own request.
/// </summary>
internal sealed partial class ApprovalVisibility
{
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly ApprovalKindCatalog catalog;
    [Inject] private readonly IServiceProvider services;

    private (string[] Kinds, string[] KindAndTypes)? scopes;

    /// <summary>The caller's user id; null for an anonymous caller (who sees nothing).</summary>
    public string? CallerId => currentUser.IsAuthenticated && currentUser.Id is { Length: > 0 } id ? id : null;

    public ClaimsPrincipal CallerPrincipal => httpContextAccessor.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());

    /// <summary>The row rule, pushed into every query and load of <see cref="ApprovalRequest"/>.</summary>
    public async Task<Expression<Func<ApprovalRequest, bool>>> RowFilterAsync(CancellationToken cancellationToken = default)
    {
        if (CallerId is not { } me)
            return _ => false;

        var (kinds, kindAndTypes) = await ScopesAsync(cancellationToken);
        Expression<Func<ApprovalRequest, bool>> filter = r =>
            r.RequestedBy == me || r.DeciderIds.Any(d => d == me) || r.VisibleTo.Any(v => v == me);
        // Empty lists are left out rather than sent as "in ()".
        if (kinds.Length > 0)
            filter = Or(filter, r => r.Kind.In(kinds));
        if (kindAndTypes.Length > 0)
            filter = Or(filter, r => r.KindAndType.In(kindAndTypes));
        return filter;
    }

    /// <summary>The row rule for one loaded request.</summary>
    public async Task<bool> MaySeeAsync(ApprovalRequest request, CancellationToken cancellationToken = default)
    {
        if (CallerId is not { } me)
            return false;
        if (request.RequestedBy == me || request.DeciderIds.Contains(me) || request.VisibleTo.Contains(me))
            return true;
        var (kinds, kindAndTypes) = await ScopesAsync(cancellationToken);
        return kinds.Contains(request.Kind) || kindAndTypes.Contains(request.KindAndType);
    }

    /// <summary>
    /// Whether the caller may decide <paramref name="request"/> now: a known kind, not the requester, and the kind's
    /// live gate. Says nothing about the status.
    /// </summary>
    public async Task<bool> MayDecideAsync(ApprovalRequest request, IAsyncDocumentSession session, CancellationToken cancellationToken = default)
    {
        if (CallerId is not { } me || request.RequestedBy == me)
            return false;
        if (catalog.Find(request.Kind) is not { } kind)
            return false;
        return await kind.MayDecideAsync(services, new ApprovalDeciderContext
        {
            Request = request.ToInfo(),
            UserId = me,
            User = CallerPrincipal,
            Session = session,
            CancellationToken = cancellationToken,
        });
    }

    /// <summary>Whether the caller may withdraw <paramref name="request"/>: only its requester.</summary>
    public bool MayWithdraw(ApprovalRequest request) => CallerId is { } me && request.RequestedBy == me;

    private async Task<(string[] Kinds, string[] KindAndTypes)> ScopesAsync(CancellationToken cancellationToken)
    {
        if (scopes is { } known)
            return known;
        var kinds = new List<string>();
        var kindAndTypes = new List<string>();
        var user = CallerPrincipal;
        foreach (var kind in catalog.All)
        {
            var scope = await kind.DecidesAllAsync(services, user, cancellationToken);
            if (scope.Everything)
                kinds.Add(kind.Name);
            else
                kindAndTypes.AddRange(scope.SubjectTypes.Select(type => KindAndType(kind.Name, type)));
        }
        scopes = (kinds.ToArray(), kindAndTypes.ToArray());
        return scopes.Value;
    }

    internal static string KindAndType(string kind, string subjectType) => kind + "|" + subjectType;

    private static Expression<Func<T, bool>> Or<T>(Expression<Func<T, bool>> left, Expression<Func<T, bool>> right)
    {
        var parameter = left.Parameters[0];
        var rightBody = new ReplaceParameter(right.Parameters[0], parameter).Visit(right.Body);
        return Expression.Lambda<Func<T, bool>>(Expression.OrElse(left.Body, rightBody), parameter);
    }

    private sealed class ReplaceParameter(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
