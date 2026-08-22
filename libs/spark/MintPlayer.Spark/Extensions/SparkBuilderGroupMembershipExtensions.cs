using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Builder;

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
}
