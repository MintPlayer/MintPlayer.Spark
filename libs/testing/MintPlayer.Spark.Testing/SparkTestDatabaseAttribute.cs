namespace MintPlayer.Spark.Testing;

/// <summary>
/// Names a database type the test assembly can run on (#536 O16). A provider testing package's build targets write
/// it, so a test project that references <c>MintPlayer.Spark.Testing.SqlServer</c> runs on SQL Server without a line
/// of its own. <see cref="SparkTestDatabases"/> reads it.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class SparkTestDatabaseAttribute : Attribute
{
    /// <param name="providerType">An <see cref="ISparkTestDatabaseProvider"/> with a public parameterless constructor.</param>
    public SparkTestDatabaseAttribute(Type providerType)
    {
        ArgumentNullException.ThrowIfNull(providerType);
        ProviderType = providerType;
    }

    public Type ProviderType { get; }
}
