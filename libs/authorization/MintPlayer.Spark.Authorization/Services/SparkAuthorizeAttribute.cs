using Microsoft.AspNetCore.Authorization;
using MintPlayer.Spark.Abstractions.Authorization;

namespace MintPlayer.Spark.Authorization.Services;

/// <summary>
/// Authorizes an MVC action or minimal-API endpoint against the same <c>security.json</c> right the
/// Spark pipeline checks.
/// <code>
/// [HttpPost("tokens")]
/// [SparkAuthorize("New", nameof(UploadToken))]
/// public Task&lt;IActionResult&gt; Create() { … }
/// </code>
/// <para>
/// <b>The right form is primary and the group form secondary</b>, deliberately. A group is an
/// implementation detail of <em>who holds</em> a right; the right is the product's authorization
/// model, an operator can change who holds it without a redeploy, and it is <em>the same string</em>
/// the persistent-object endpoints check — so a controller and its equivalent Spark endpoint
/// provably agree rather than agreeing by convention.
/// </para>
/// <para>
/// Before this existed there was no <c>[Authorize]</c> interop at all: <c>UseSpark()</c> registers a
/// bare <c>AddAuthorization()</c> with no policies, so <c>[Authorize(Policy = "Administrators")]</c>
/// threw at request time. Two things worked by accident — a bare <c>[Authorize]</c>, and
/// <c>[Authorize(Roles = …)]</c> <em>if</em> the group happened to be stored as an ASP.NET Identity
/// role. A group carried as a <c>group</c> claim (what the identity provider, the E2E fixtures and
/// module certificates all use) was invisible to <c>RequireRole</c>. That inconsistency is its own
/// trap: anyone testing against a role-shaped fixture concludes interop already works.
/// </para>
/// <para>
/// Implemented through <see cref="IAuthorizationRequirementData"/>, so the attribute <em>is</em> the
/// requirement — no policy provider, and no policy-name strings to keep in step with anything.
/// <c>[AllowAnonymous]</c> still wins, as it does for any authorization policy.
/// </para>
/// </summary>
/// <remarks>
/// Derives from <see cref="AuthorizeAttribute"/> because that is what makes it <em>visible</em>:
/// <c>AuthorizationMiddleware</c> collects <see cref="IAuthorizeData"/> from endpoint metadata, and
/// an attribute implementing only <see cref="IAuthorizationRequirementData"/> is never looked at.
/// This is the shape ASP.NET Core's own sample uses, and getting it wrong fails open — the endpoint
/// simply is not authorized at all.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class SparkAuthorizeAttribute : AuthorizeAttribute, IAuthorizationRequirement, IAuthorizationRequirementData
{
    /// <summary>Authorizes against the right <c>{action}/{target}</c>, e.g. <c>Read/Person</c>.</summary>
    public SparkAuthorizeAttribute(string action, string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        Action = action;
        Target = target;
    }

    /// <summary>
    /// Authorizes against group membership alone. Prefer the right form: this one names a group
    /// directly in code, so changing who may do something means a redeploy.
    /// </summary>
    public SparkAuthorizeAttribute() { }

    /// <summary>The action half of the right — <c>Read</c>, <c>Query</c>, <c>Edit</c>, <c>New</c>, <c>Delete</c>,
    /// or one of the combined forms <c>security.json</c> accepts.</summary>
    public string? Action { get; }

    /// <summary>The target half of the right, normally an entity type name.</summary>
    public string? Target { get; }

    /// <summary>
    /// The group the caller must belong to, resolved through <see cref="IGroupMembershipProvider"/>.
    /// Combines with the right form when both are given: both must pass.
    /// </summary>
    public string? Group { get; set; }

    /// <summary>The right this attribute demands, or null when it demands only group membership.</summary>
    internal string? Resource => Action is null || Target is null ? null : $"{Action}/{Target}";

    IEnumerable<IAuthorizationRequirement> IAuthorizationRequirementData.GetRequirements() => [this];
}

/// <summary>
/// Evaluates <see cref="SparkAuthorizeAttribute"/> against the running application's authorization
/// model.
/// </summary>
/// <remarks>
/// Resolves <see cref="IAccessControl"/> per evaluation from the request's own scope rather than
/// holding one: the handler is registered as a singleton (ASP.NET Core's convention for
/// authorization handlers) while <c>IAccessControl</c> is scoped, and capturing a scoped service in
/// a singleton is how a decision starts being made against the wrong request's principal.
/// </remarks>
internal sealed class SparkAuthorizeHandler(IServiceProvider serviceProvider)
    : AuthorizationHandler<SparkAuthorizeAttribute>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, SparkAuthorizeAttribute requirement)
    {
        var scoped = (context.Resource as HttpContext)?.RequestServices ?? serviceProvider;

        if (requirement.Resource is { } resource)
        {
            var accessControl = scoped.GetRequiredService<IAccessControl>();
            if (!await accessControl.IsAllowedAsync(resource))
                return;   // Not Fail(): another handler for another requirement may still succeed.
        }

        if (requirement.Group is { } group)
        {
            var membership = scoped.GetService<IGroupMembershipProvider>();
            if (membership is null)
                return;

            var groups = await membership.GetCurrentUserGroupsAsync();
            if (!groups.Contains(group, StringComparer.OrdinalIgnoreCase))
                return;
        }

        context.Succeed(requirement);
    }
}
