namespace MintPlayer.Spark.Testing;

/// <summary>
/// A <see cref="FactAttribute"/> for a test that exercises a RavenDB feature the AGPL (unlicensed)
/// server does not offer — ETL, encryption, documents compression, data archival, backups. Skipped,
/// with a reason, when no licence is available.
/// </summary>
/// <remarks>
/// This exists so that a contributor who cannot have a licence still gets a green, honest run.
/// Organization secrets are not exposed to <c>pull_request</c> runs from forks, so on a fork PR the
/// licence is absent by construction; the same is true of a first-time contributor running the
/// suite locally.
/// <para>
/// The skip is deliberately expressed <em>per test</em> rather than as a condition on the CI job.
/// GitHub treats a skipped job as a passing required status check, so an <c>if:</c>-skipped job
/// reports green while having verified nothing. A test skipped this way instead lands in the TRX as
/// a skip, where it is counted and visible.
/// </para>
/// <para>
/// Prefer scoping a test so that only its licensed assertion needs this. Most of what looks like a
/// licensed test is not: authorization over a licensed feature — checking that an ETL deployment is
/// <em>refused</em> — never reaches the feature, and should stay an ordinary <c>[Fact]</c> that runs
/// everywhere.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequiresLicensedFeatureAttribute : FactAttribute
{
    /// <param name="feature">
    /// The licensed feature, named as RavenDB names it (e.g. <c>"RavenDB ETL"</c>). It appears in
    /// the skip reason, so that a skipped run says what was not covered.
    /// </param>
    public RequiresLicensedFeatureAttribute(string feature)
    {
        if (!LicenseHelper.IsPresent)
        {
            Skip = $"Requires a RavenDB licence that includes {feature}. " +
                   "No licence found, so the embedded server is running in AGPL mode where this " +
                   "feature is unavailable. This test runs on the trusted CI path.";
        }
    }
}
