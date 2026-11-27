using System.Reflection;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Tests.Layering;

/// <summary>
/// Core knows no store (#536 D34): every provider detail sits behind a contract a provider package implements. A
/// reference from core, its abstractions or the provider-neutral feature packages to a database client fails here,
/// instead of quietly making every app ship that client.
/// </summary>
public class ProviderNeutralCoreTests
{
    private static readonly string[] DatabaseClients =
        ["Raven.Client", "Raven.TestDriver", "Microsoft.EntityFrameworkCore", "Npgsql", "Microsoft.Data.SqlClient", "MintPlayer.Spark.RavenDB", "MintPlayer.Spark.Relational"];

    public static TheoryData<string> NeutralAssemblies => new()
    {
        typeof(ISparkStore).Assembly.GetName().Name!,                               // MintPlayer.Spark.Abstractions
        typeof(SparkExtensions).Assembly.GetName().Name!,                           // MintPlayer.Spark
        typeof(MintPlayer.Spark.Migrations.ISparkMigration).Assembly.GetName().Name!, // MintPlayer.Spark.Migrations
        typeof(MintPlayer.Spark.Testing.SparkTestDriver).Assembly.GetName().Name!,  // MintPlayer.Spark.Testing (D35)
    };

    [Theory]
    [MemberData(nameof(NeutralAssemblies))]
    public void References_no_database_client(string assemblyName)
    {
        var references = Assembly.Load(assemblyName).GetReferencedAssemblies().Select(a => a.Name!).ToList();

        references.Where(name => DatabaseClients.Any(client => name.StartsWith(client, StringComparison.Ordinal)))
            .Should().BeEmpty($"{assemblyName} must reach a store only through the storage contracts");
    }
}
