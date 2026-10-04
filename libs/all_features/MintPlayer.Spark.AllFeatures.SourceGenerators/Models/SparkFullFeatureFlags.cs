using MintPlayer.ValueComparerGenerator.Attributes;

namespace MintPlayer.Spark.AllFeatures.SourceGenerators.Models;

[GenerateEquality]
public partial class SparkFullFeatureFlags
{
    public bool HasSpark { get; set; }
    /// <summary>
    /// Whether the Authorization package itself is referenced. Keyed on a type that lives only there:
    /// <c>SparkUser</c> also resolves through MintPlayer.Spark.Authorization.Abstractions (#388), e.g. via
    /// an entity library, and an <c>AddAuthentication</c> call into an unreferenced package would not compile.
    /// </summary>
    public bool HasAuthorization { get; set; }
    public bool HasMessaging { get; set; }
    public bool HasReplication { get; set; }
    /// <summary>Whether a referenced package ships an <c>ISparkMigration</c> (#388), so <c>AddMigrations</c> is due even without the app's own.</summary>
    public bool HasReferencedMigrations { get; set; }
}
