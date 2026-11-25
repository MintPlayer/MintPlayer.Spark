namespace MintPlayer.Spark.Testing;

/// <summary>
/// The xUnit trait that marks a provider-neutral test class (#536 M9): one that runs on whichever database
/// <see cref="SparkTestDatabases"/> picks. <see cref="SparkTestDriver"/> and <see cref="SparkSharedTestDriver"/> carry it,
/// and a class on <see cref="SparkSharedDatabase"/> alone (no driver base) declares it itself:
/// <code>
/// [Trait(SparkTestTraits.Database, SparkTestTraits.Any)]
/// </code>
/// A CI leg per database then runs only those classes, with <c>dotnet test --filter SparkDatabase=Any</c> and
/// <c>SPARK_TEST_DATABASE</c> naming the database. A RavenDB-only class (on <c>RavenSparkTestDriver</c>) has no trait,
/// so that leg leaves it out instead of running it on RavenDB a second time.
/// </summary>
public static class SparkTestTraits
{
    /// <summary>The trait's name.</summary>
    public const string Database = "SparkDatabase";

    /// <summary>The value of a class that runs on any database.</summary>
    public const string Any = "Any";
}
