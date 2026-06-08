using MintPlayer.Spark.Migrations;

namespace MintPlayer.Spark.Tests.Migrations;

/// <summary>
/// Pure coverage for <see cref="SparkMigrationRegistry"/>: the ascending-by-version getter and the
/// duplicate-version guard. <c>Add</c> is internal but reachable via the project's
/// <c>InternalsVisibleTo("MintPlayer.Spark.Tests")</c>.
/// </summary>
public class SparkMigrationRegistryTests
{
    private sealed class Dummy : ISparkMigration
    {
        public static long Version => 0;
        public Task UpAsync(CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public void Migrations_are_returned_in_ascending_version_order()
    {
        var registry = new SparkMigrationRegistry();

        registry.Add(new SparkMigrationDescriptor(typeof(Dummy), 30, "C", null));
        registry.Add(new SparkMigrationDescriptor(typeof(Dummy), 10, "A", null));
        registry.Add(new SparkMigrationDescriptor(typeof(Dummy), 20, "B", null));

        registry.Migrations.Select(m => m.Version).Should().Equal(10, 20, 30);
        registry.Migrations.Select(m => m.Name).Should().Equal("A", "B", "C");
    }

    [Fact]
    public void Duplicate_version_throws_InvalidOperationException()
    {
        var registry = new SparkMigrationRegistry();
        registry.Add(new SparkMigrationDescriptor(typeof(Dummy), 42, "First", null));

        var act = () => registry.Add(new SparkMigrationDescriptor(typeof(Dummy), 42, "Second", null));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*42*")
            .WithMessage("*First*")
            .WithMessage("*Second*");
    }
}
