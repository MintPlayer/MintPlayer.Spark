using Microsoft.AspNetCore.Authorization;
using MintPlayer.Spark.Abstractions.Authorization;

namespace MintPlayer.Spark.Services;

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
/// <para>
/// ⚠️ <b>The scope comes from <c>context.Resource as HttpContext</c>, which is an HTTP assumption</b>
/// (#327 §9.9). On a <b>SignalR hub</b> the resource is a <c>HubInvocationContext</c>, not an
/// <c>HttpContext</c>, so the fallback fires and the handler resolves <c>IAccessControl</c> from the
/// <em>root</em> provider — where a scoped service cannot be resolved at all, and the invocation
/// throws rather than denying. Latent today: Spark ships no hubs and nothing in the repo puts
/// <c>[SparkAuthorize]</c> on one. Recorded here rather than fixed speculatively, because the fix
/// depends on how hubs would be hosted; the shape of it is to prefer
/// <c>HubInvocationContext.Context.GetHttpContext()?.RequestServices</c> before falling back.
/// </para>
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
            RefuseWellKnownGroup(scoped, group);

            var membership = scoped.GetService<IGroupMembershipProvider>();
            if (membership is null)
                return;

            var groups = await membership.GetCurrentUserGroupsAsync();
            if (!groups.Contains(group, StringComparer.OrdinalIgnoreCase))
                return;
        }

        context.Succeed(requirement);
    }

    /// <summary>
    /// Refuses <c>[SparkAuthorize(Group = …)]</c> naming a group that <c>security.json</c> declared
    /// as <c>anonymous</c> or <c>authenticated</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ Such an attribute cannot work, and — this is why it throws rather than denying — it does
    /// not look broken. Well-known ids are deliberately excluded from claim-derived membership
    /// (<c>anonymous</c> and <c>authenticated</c> are decided from authentication state, never from
    /// a claim), so this comparison can never match and the endpoint denies <em>everyone, forever</em>,
    /// with a 403 that is indistinguishable from a correctly configured refusal. An author reading
    /// the attribute sees "signed-in users may do this" and reads the 403 as their own account
    /// lacking something.
    /// <para>
    /// Throwing on the first evaluation turns a permanent silent lockout into one loud startup-shaped
    /// error naming the right fix — which is the right form of the attribute, not a different group.
    /// </para>
    /// </remarks>
    private static void RefuseWellKnownGroup(IServiceProvider scoped, string group)
    {
        var loader = scoped.GetService<ISecurityConfigurationLoader>();
        if (loader is null) return;

        var config = loader.GetConfiguration();
        if (config.WellKnown is not { Count: > 0 } wellKnown) return;

        foreach (var (role, groupId) in wellKnown)
        {
            // Match the declared id directly, and the display name it resolves to — an author is
            // far likelier to write the readable name than the GUID.
            var matchesId = string.Equals(groupId, group, StringComparison.OrdinalIgnoreCase);
            var matchesName = config.Groups.TryGetValue(groupId, out var name)
                && name.Translations.Values.Any(v => string.Equals(v, group, StringComparison.OrdinalIgnoreCase));

            if (!matchesId && !matchesName) continue;

            throw new InvalidOperationException(
                $"[SparkAuthorize(Group = \"{group}\")] names the group security.json declares as " +
                $"'{role}'. Well-known groups are decided from authentication state, not from group " +
                $"membership, so they are excluded from the membership this attribute checks — the " +
                $"requirement could never be satisfied and the endpoint would deny every caller with " +
                $"a 403 that looks like an ordinary refusal. Use the right form instead: " +
                $"[SparkAuthorize(\"<action>\", \"<target>\")], and grant that right to the " +
                $"'{role}' group in security.json.");
        }
    }
}
