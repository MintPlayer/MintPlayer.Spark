using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Abstractions.Authorization
{
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
}

namespace MintPlayer.Spark.Extensions
{
    /// <summary>
    /// Composing group-membership providers. In Abstractions rather than core (where
    /// <c>UseGroupMembershipProvider</c> and the default claims provider live) so a package that references only
    /// Abstractions can add one: the Authorization package's store-backed groups for agent tokens (MCP PRD §4.6).
    /// The namespace is core's, so callers did not change when it moved.
    /// </summary>
    public static class SparkGroupMembershipCompositionExtensions
    {
        /// <summary>
        /// Adds a group-membership provider whose answers are <b>merged</b> with the primary one's —
        /// the claims provider by default, or whatever <c>UseGroupMembershipProvider&lt;T&gt;()</c>
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
}
