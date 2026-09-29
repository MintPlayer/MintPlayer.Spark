using MintPlayer.Spark.Abstractions.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Extensions;

public static class SparkBuilderGroupMembershipExtensions
{
    /// <summary>
    /// Replaces how Spark decides which groups the current caller belongs to — the input to every
    /// <c>security.json</c> decision. The default reads <c>group</c>/<c>groups</c> claims; supply
    /// your own to resolve them from Identity roles, a directory, or a database.
    /// </summary>
    /// <remarks>
    /// Lives in core because the default provider does: an application can own a security model
    /// without taking a dependency on the Authorization package, and it must be able to say where
    /// group membership comes from.
    /// <para>
    /// Removing the previous registration is the part worth owning here: leaving both in the
    /// container makes which provider runs depend on registration order.
    /// </para>
    /// <para>
    /// A custom provider cannot hand a caller a well-known role. The ids declared in
    /// <c>wellKnown</c> are excluded from claim-derived membership, so returning "Signed-in users"
    /// resolves nothing — <c>anonymous</c> and <c>authenticated</c> are decided from authentication
    /// state alone.
    /// </para>
    /// </remarks>
    public static ISparkBuilder UseGroupMembershipProvider<TProvider>(this ISparkBuilder builder)
        where TProvider : class, IGroupMembershipProvider
    {
        var existing = builder.Services.FirstOrDefault(d => d.ServiceType == typeof(IGroupMembershipProvider));
        if (existing != null)
            builder.Services.Remove(existing);

        builder.Services.AddScoped<IGroupMembershipProvider, TProvider>();
        return builder;
    }

    /// <summary>
    /// Adds a group-membership provider whose answers are <b>merged</b> with the primary one's —
    /// the claims provider by default, or whatever <see cref="UseGroupMembershipProvider{TProvider}"/>
    /// installed. Use it for membership that comes from somewhere other than the sign-in, such as
    /// privileges earned by reputation (#460, D12), without taking over claim-based groups.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A composed provider may return group <em>names</em>, and also group <em>ids</em> by
    /// implementing <see cref="IGroupIdMembershipProvider"/>. Either way the well-known ids are
    /// dropped: a composed provider can no more hand out <c>anonymous</c> or <c>authenticated</c>
    /// than the primary one can.
    /// </para>
    /// <para>
    /// Every provider is asked once per request; the merged answer is cached for the rest of it and
    /// shared with <c>[SparkAuthorize(Group = …)]</c>. Adding the same provider type twice is a
    /// no-op. The order of the calls does not matter: <c>UseGroupMembershipProvider</c> replaces only
    /// the primary provider and leaves composed ones in place.
    /// </para>
    /// </remarks>
    public static ISparkBuilder AddGroupMembershipProvider<TProvider>(this ISparkBuilder builder)
        where TProvider : class, IGroupMembershipProvider
    {
        if (builder.Services.Any(d => d.ServiceType == typeof(IComposedGroupMembershipProvider)
                && d.ImplementationType == typeof(ComposedGroupMembershipProvider<TProvider>)))
        {
            return builder;
        }

        builder.Services.TryAddScoped<TProvider>();
        builder.Services.AddScoped<IComposedGroupMembershipProvider, ComposedGroupMembershipProvider<TProvider>>();
        return builder;
    }
}
