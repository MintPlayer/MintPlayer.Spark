using CodeCoverage.Entities;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;

namespace CodeCoverage.Actions;

/// <summary>The platform lifecycle page (#568 M3, D4): public, read-only data the app collects itself.</summary>
public partial class PlatformReleaseActions : DefaultPersistentObjectActions<PlatformRelease>, ISparkOwnsRowSecurity
{
    /// <inheritdoc />
    public string RowSecurityRationale =>
        "Every row is public by design: PlatformLifecycleCronJob copies what the platforms publish themselves (the .NET " +
        "releases index, endoflife.date, npm dist-tags), and no row belongs to an account or a repository. security.json " +
        "grants only QueryRead/PlatformRelease, to anonymous and signed-in users alike; nobody can create, edit or delete " +
        "a row, which only the collector writes.";
}
