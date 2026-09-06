using MintPlayer.Spark.Services;
using MintPlayer.Spark.Services.Breadcrumb;
using NSubstitute;

namespace MintPlayer.Spark.Tests._Infrastructure;

/// <summary>
/// Builds a real <see cref="RowSecurityGate"/> over whichever doubles a test already has.
/// </summary>
/// <remarks>
/// Deliberately the real gate rather than a stub. The gate <em>is</em> the enforcement now — filter,
/// breadcrumb, map, redact, in that fixed order — so substituting it would put a test back where the
/// permissive row-security double had it: unable to tell "enforced" from "never asked". Passing the
/// test's own <c>IRowSecurity</c> through means a recording double still records and a denying double
/// still denies.
/// </remarks>
internal static class TestRowSecurityGate
{
    public static IRowSecurityGate For(
        IRowSecurity rowSecurity,
        IEntityMapper? entityMapper = null,
        IBreadcrumbResolver? breadcrumbResolver = null)
        => new RowSecurityGate(
            rowSecurity,
            entityMapper ?? Substitute.For<IEntityMapper>(),
            breadcrumbResolver ?? Substitute.For<IBreadcrumbResolver>());
}
