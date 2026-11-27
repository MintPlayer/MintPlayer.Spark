using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Testing;

/// <summary>#536 O16: how a test assembly picks its database type. It never falls back to a default.</summary>
public class SparkTestDatabasesTests
{
    private static readonly Type Raven = typeof(RavenSparkTestDatabaseProvider);
    private static readonly Type InMemory = typeof(InMemorySparkTestDatabaseProvider);

    [Fact]
    public void One_attribute_is_the_choice()
        => SparkTestDatabases.Select("T", [Raven], variable: null).Should().Be(Raven);

    [Fact]
    public void The_variable_picks_one_of_several_ignoring_case()
        => SparkTestDatabases.Select("T", [Raven, InMemory], variable: "inmemory").Should().Be(InMemory);

    [Fact]
    public void Several_without_the_variable_fail_naming_the_candidates()
        => new Action(() => SparkTestDatabases.Select("T", [Raven, InMemory], variable: null))
            .Should().Throw<InvalidOperationException>().WithMessage("*InMemory, RavenDB*SPARK_TEST_DATABASE*");

    [Fact]
    public void A_variable_naming_an_unreferenced_provider_fails()
        => new Action(() => SparkTestDatabases.Select("T", [Raven], variable: "SqlServer"))
            .Should().Throw<InvalidOperationException>().WithMessage("*SPARK_TEST_DATABASE=SqlServer*Candidates: RavenDB*");

    [Fact]
    public void No_attribute_fails()
        => new Action(() => SparkTestDatabases.Select("T", [], variable: null))
            .Should().Throw<InvalidOperationException>().WithMessage("*names no database type*");

    [Fact]
    public void A_type_that_is_no_provider_fails()
        => new Action(() => SparkTestDatabases.Select("T", [typeof(string)], variable: null))
            .Should().Throw<InvalidOperationException>().WithMessage("*System.String*ISparkTestDatabaseProvider*");

    [Fact]
    public void This_assembly_names_all_four_providers()
        => typeof(SparkTestDatabasesTests).Assembly.GetCustomAttributes(typeof(SparkTestDatabaseAttribute), false)
            .Cast<SparkTestDatabaseAttribute>().Select(a => a.ProviderType.Name)
            .Should().BeEquivalentTo([
                nameof(RavenSparkTestDatabaseProvider), nameof(InMemorySparkTestDatabaseProvider),
                nameof(SqlServerSparkTestDatabaseProvider), nameof(PostgreSqlSparkTestDatabaseProvider)]);
}
