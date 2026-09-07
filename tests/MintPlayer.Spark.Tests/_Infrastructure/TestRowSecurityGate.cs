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
    /// <param name="entityMapper">
    /// <b>Required, and it must be the executor's own.</b> Mapping happens inside the gate now, so a
    /// gate handed a different mapper produces different rows than the test configured — and when
    /// that mapper is an unconfigured substitute it produces <see langword="null"/> rows, which
    /// surface as a NullReferenceException deep in sorting rather than as a wiring mistake. There is
    /// deliberately no default.
    /// </param>
    public static IRowSecurityGate For(
        IRowSecurity rowSecurity,
        IEntityMapper entityMapper,
        IBreadcrumbResolver? breadcrumbResolver = null)
        => new RowSecurityGate(
            rowSecurity,
            entityMapper,
            breadcrumbResolver ?? Substitute.For<IBreadcrumbResolver>());
}
