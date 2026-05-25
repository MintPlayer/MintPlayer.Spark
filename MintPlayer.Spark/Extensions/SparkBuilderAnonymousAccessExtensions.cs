using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Extensions;

public static class SparkBuilderAnonymousAccessExtensions
{
    /// <summary>
    /// Replaces the default fail-closed <see cref="IAccessControl"/> with one that
    /// allows every action. Use only for demos, prototypes, or apps that
    /// intentionally have no authorization model.
    /// <para>
    /// Apps that need real authorization should call <c>spark.AddAuthorization()</c>
    /// (from the <c>MintPlayer.Spark.Authorization</c> package) instead.
    /// </para>
    /// <para>
    /// Calling neither leaves the deny-all default in place — every Spark CRUD,
    /// query, and custom-action call is refused.
    /// </para>
    /// </summary>
    public static ISparkBuilder AllowAnonymousAccess(this ISparkBuilder builder)
    {
        builder.Services.AddScoped<IAccessControl, AllowAllAccessControl>();
        return builder;
    }
}
