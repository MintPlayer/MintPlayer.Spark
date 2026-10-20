using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Migrations;

namespace MintPlayer.Spark.Tests.Migrations;

/// <summary>
/// X20 item 9: a migration's version is its class name's <c>M_{yyyyMMddHHmm}_{Name}</c> timestamp, and
/// registration refuses a class that is not named that way.
/// </summary>
public class SparkMigrationVersionTests
{
    private sealed class M_202610191200_BackfillOrderTotals : ISparkMigration
    {
        public Task UpAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class BackfillOrderTotals : ISparkMigration
    {
        public Task UpAsync(CancellationToken ct) => Task.CompletedTask;
    }

    // Twelve digits, but no such minute: month 13.
    private sealed class M_202613011200_NoSuchMonth : ISparkMigration
    {
        public Task UpAsync(CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public void The_version_is_the_class_names_timestamp()
        => SparkMigrationVersion.Of<M_202610191200_BackfillOrderTotals>().Should().Be(202610191200L);

    [Theory]
    [InlineData("M_202610191200_X", true, 202610191200L)]
    [InlineData("M_202610191200_Backfill_Order_Totals2", true, 202610191200L)]
    [InlineData("M_20261019120_Short", false, 0L)]
    [InlineData("M_2026101912000_Long", false, 0L)]
    [InlineData("M_202610191200", false, 0L)]
    [InlineData("M_202610191200_", false, 0L)]
    [InlineData("202610191200_NoPrefix", false, 0L)]
    [InlineData("M_202610192400_NoSuchHour", false, 0L)]
    [InlineData("M_202602301200_NoSuchDay", false, 0L)]
    [InlineData("", false, 0L)]
    public void TryParse_accepts_only_M_timestamp_name(string className, bool expected, long version)
    {
        SparkMigrationVersion.TryParse(className, out var parsed).Should().Be(expected);
        parsed.Should().Be(version);
    }

    [Fact]
    public void Registering_a_class_without_a_timestamp_name_is_refused()
    {
        var builder = new SparkBuilder(new ServiceCollection());

        var act = () => builder.AddMigrations(m => m.AddMigration<BackfillOrderTotals>());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*BackfillOrderTotals*M_{yyyyMMddHHmm}_{Name}*");
    }

    [Fact]
    public void Registering_a_class_whose_timestamp_is_no_real_minute_is_refused()
    {
        var builder = new SparkBuilder(new ServiceCollection());

        var act = () => builder.AddMigrations(m => m.AddMigration<M_202613011200_NoSuchMonth>());

        act.Should().Throw<InvalidOperationException>().WithMessage("*M_202613011200_NoSuchMonth*");
    }

    [Fact]
    public void A_registered_migration_carries_its_derived_version_and_class_name()
    {
        var builder = new SparkBuilder(new ServiceCollection());

        builder.AddMigrations(m => m.AddMigration<M_202610191200_BackfillOrderTotals>());

        var registry = (SparkMigrationRegistry)builder.Services
            .Single(d => d.ServiceType == typeof(SparkMigrationRegistry)).ImplementationInstance!;
        var migration = registry.Migrations.Should().ContainSingle().Which;
        migration.Version.Should().Be(202610191200L);
        migration.Name.Should().Be(nameof(M_202610191200_BackfillOrderTotals));
    }
}
