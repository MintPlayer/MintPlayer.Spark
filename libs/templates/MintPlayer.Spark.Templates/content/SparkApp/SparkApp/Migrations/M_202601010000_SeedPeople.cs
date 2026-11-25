using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Migrations;
using SparkApp.Library.Entities;

namespace SparkApp.Migrations;

/// <summary>
/// A sample migration: it stores two people the first time the app starts against an empty database, and never again.
/// Migrations run in Version order; name each class after its version so the folder reads in order.
/// It writes through ISparkStore, so it runs on whichever store Program.cs picks.
/// </summary>
public partial class M_202601010000_SeedPeople : ISparkMigration
{
    public static long Version => 202601010000;
    public static string? Description => "Seed two sample people";

    [Inject] private readonly ISparkStore store;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        using var session = store.OpenSession();
        await session.StoreAsync(new Person { FirstName = "Ada", LastName = "Lovelace" }, cancellationToken);
        await session.StoreAsync(new Person { FirstName = "Alan", LastName = "Turing" }, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
    }
}
