using Microsoft.EntityFrameworkCore;
using MintPlayer.Spark.Relational.Schema;
using MintPlayer.Spark.Relational.Storage;
using MintPlayer.Spark.Relational.Tests._Infrastructure;

namespace MintPlayer.Spark.Relational.Tests.Schema;

/// <summary>
/// O19 on a real database: an attribute renamed with a new type keeps its column's data where the database can convert
/// it, and the migration fails, not silently, where it can't. V1 and V2 map one table, <c>RenScores</c>; the attribute
/// id says which of V1's columns V2's column is.
/// </summary>
public abstract class RenamedAttributeTests
{
    protected abstract RelationalTestServer? Server { get; }

    private RelationalTestServer RequireServer() => Server ?? throw new SkipException("No server is configured for this provider.");

    /// <summary>V1 with one row, then a store over the same database with V2's model; the caller applies V1 → V2.</summary>
    private static async Task<(RelationalTestDatabase V1, RelationalSparkStore V2)> V1ThenV2<TV1, TV2>(
        RelationalTestServer server, string v1Property, string v2Property, TV1 row) where TV1 : class
    {
        var attribute = Guid.NewGuid();
        var name = "spark_rel_" + Guid.NewGuid().ToString("N")[..12];
        var v1 = await RelationalTestDatabase.CreateAsync(server, [typeof(TV1)], mb => mb.Entity(typeof(TV1)).ToTable("RenScores"), name,
            attributeId: (t, p) => t == typeof(TV1) && p == v1Property ? attribute : null);
        using (var write = v1.Store.OpenSession())
        {
            await write.StoreAsync(row);
            await write.SaveChangesAsync();
        }
        var v2 = RelationalTestDatabase.Open(server, [typeof(TV2)], mb => mb.Entity(typeof(TV2)).ToTable("RenScores"), name,
            attributeId: (t, p) => t == typeof(TV2) && p == v2Property ? attribute : null);
        return (v1, v2);
    }

    [SkippableFact]
    public async Task A_renamed_attribute_keeps_its_data_through_a_type_change_the_database_can_convert()
    {
        var (v1, v2) = await V1ThenV2<RenScoreV1, RenScoreV2>(RequireServer(), nameof(RenScoreV1.Score), nameof(RenScoreV2.Points),
            new RenScoreV1 { Id = "RenScores/one", Score = 42 });
        await using (v1)
        {
            await using (var target = v2.NewContext())
            await using (var source = v1.Store.NewContext())
                await RelationalSchema.ApplyDifferencesAsync(target, source);

            using var session = v2.OpenSession();
            var loaded = await session.LoadAsync<RenScoreV2>("RenScores/one");

            loaded.Should().NotBeNull();
            loaded!.Points.Should().Be(42L);
        }
    }

    [SkippableFact]
    public async Task A_renamed_attribute_whose_data_cannot_convert_fails_the_migration()
    {
        var (v1, v2) = await V1ThenV2<RenScoreV1, RenScoreV3>(RequireServer(), nameof(RenScoreV1.Label), nameof(RenScoreV3.Number),
            new RenScoreV1 { Id = "RenScores/one", Score = 1, Label = "not a number" });
        await using (v1)
        {
            await using var target = v2.NewContext();
            await using var source = v1.Store.NewContext();

            var failure = await Record.ExceptionAsync(() => RelationalSchema.ApplyDifferencesAsync(target, source));

            // PostgreSQL refuses the type change itself (42804, "cannot be cast automatically": no USING clause), even for
            // text that holds numbers; SQL Server converts value by value and refuses this one.
            (failure is System.Data.Common.DbException).Should().BeTrue();
            failure!.Message.Should().Match(m => m.Contains("cannot be cast automatically", StringComparison.Ordinal)
                || m.Contains("Conversion failed", StringComparison.Ordinal));
        }
    }
}

public class RenScoreV1
{
    public string? Id { get; set; }
    public int Score { get; set; }
    public string Label { get; set; } = string.Empty;
}

/// <summary>V1's Score, renamed Points and widened to a long; Label is gone.</summary>
public class RenScoreV2
{
    public string? Id { get; set; }
    public long Points { get; set; }
}

/// <summary>V1's Label, renamed Number and made a number; Score is gone.</summary>
public class RenScoreV3
{
    public string? Id { get; set; }
    public int Number { get; set; }
}

public sealed class SqlServerRenamedAttributeTests : RenamedAttributeTests
{
    protected override RelationalTestServer? Server => RelationalTestServer.SqlServer;
}

public sealed class PostgreSqlRenamedAttributeTests : RenamedAttributeTests
{
    protected override RelationalTestServer? Server => RelationalTestServer.PostgreSql;
}
