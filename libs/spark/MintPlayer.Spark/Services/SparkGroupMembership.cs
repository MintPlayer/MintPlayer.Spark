using System.Security.Claims;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;

namespace MintPlayer.Spark.Services;

/// <summary>
/// An additional <see cref="IGroupMembershipProvider"/>, registered by
/// <c>AddGroupMembershipProvider&lt;T&gt;()</c>. Its answers are merged with the primary provider's.
/// </summary>
internal interface IComposedGroupMembershipProvider
{
    IGroupMembershipProvider Provider { get; }
}

/// <inheritdoc cref="IComposedGroupMembershipProvider"/>
/// <remarks>
/// Generic so a registration is identifiable by its implementation type — which is what lets
/// <c>AddGroupMembershipProvider&lt;T&gt;()</c> refuse to add the same provider twice.
/// </remarks>
internal sealed class ComposedGroupMembershipProvider<TProvider>(TProvider provider) : IComposedGroupMembershipProvider
    where TProvider : class, IGroupMembershipProvider
{
    public IGroupMembershipProvider Provider => provider;
}

/// <summary>
/// What every group-membership provider says about the current caller, asked once per request.
/// </summary>
/// <param name="Names">Group names from every provider, in provider order, duplicates removed.</param>
/// <param name="Ids">Group ids from every provider implementing <see cref="IGroupIdMembershipProvider"/>.</param>
internal sealed record SparkRequestGroups(IReadOnlyList<string> Names, IReadOnlySet<Guid> Ids)
{
    public static readonly SparkRequestGroups None = new([], new HashSet<Guid>());
}

/// <summary>
/// Asks the group-membership providers — the primary one (<c>UseGroupMembershipProvider</c>,
/// replace semantics; the claims provider by default) and every composed one
/// (<c>AddGroupMembershipProvider</c>) — and caches the merged answer for the rest of the request.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a per-request cache.</b> <c>SecurityFileAccessControl</c> answers ~45 authorization
/// questions in one request, and each used to ask the provider again. Harmless for the claims
/// provider, not for one that reads a document per call (earned privileges, #460). Cached here, the
/// provider cost is paid once per request, and the <c>[SparkAuthorize(Group = …)]</c> handler reads the
/// same snapshot rather than asking again.
/// </para>
/// <para>
/// The cache is keyed on the <see cref="ClaimsPrincipal"/> instance, so an endpoint that replaces
/// <c>HttpContext.User</c> mid-request (a sign-in) is asked again rather than answered with the
/// previous caller's groups. Scoped, so nothing survives the request.
/// </para>
/// <para>
/// Registered by hand in <c>AddSpark</c>: the <c>[Register]</c> generator does not register a
/// class as itself.
/// </para>
/// </remarks>
internal partial class SparkGroupMembership
{
    [Inject] private readonly IGroupMembershipProvider primary;
    [Inject] private readonly IEnumerable<IComposedGroupMembershipProvider> composed;
    [Inject] private readonly IHttpContextAccessor? httpContextAccessor;

    private (ClaimsPrincipal? Principal, Task<SparkRequestGroups> Groups)? cache;

    public Task<SparkRequestGroups> GetAsync(CancellationToken cancellationToken = default)
    {
        var principal = httpContextAccessor?.HttpContext?.User;

        if (cache is { } hit && ReferenceEquals(hit.Principal, principal))
            return hit.Groups;

        var groups = LoadAsync(cancellationToken);
        cache = (principal, groups);
        return groups;
    }

    private async Task<SparkRequestGroups> LoadAsync(CancellationToken cancellationToken)
    {
        var names = new List<string>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<Guid>();

        foreach (var provider in Providers())
        {
            foreach (var name in await provider.GetCurrentUserGroupsAsync(cancellationToken))
            {
                if (seenNames.Add(name))
                    names.Add(name);
            }

            if (provider is IGroupIdMembershipProvider idProvider)
            {
                foreach (var id in await idProvider.GetCurrentUserGroupIdsAsync(cancellationToken))
                    ids.Add(id);
            }
        }

        return names.Count == 0 && ids.Count == 0 ? SparkRequestGroups.None : new SparkRequestGroups(names, ids);
    }

    private IEnumerable<IGroupMembershipProvider> Providers()
    {
        yield return primary;
        foreach (var registration in composed)
            yield return registration.Provider;
    }
}
