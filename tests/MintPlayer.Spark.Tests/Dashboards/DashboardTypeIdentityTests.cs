using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Dashboards;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Dashboards;

/// <summary>
/// D28: a dashboard finds its model type by identity. The package looked it up by name
/// (<c>GetEntityTypeByName(dashboard.Name)</c>), so once an application renamed <c>DatabaseStatistics</c> the
/// dashboard had no type, and an application type that took the old name would have been served in its place. The
/// declared name keeps naming the actions class and the stored history (<c>SparkDashboards/{declared name}</c>), which
/// is why no stored dashboard is orphaned by a rename.
/// </summary>
public class DashboardTypeIdentityTests
{
    private static readonly EntityTypeDefinition Renamed = new()
    {
        Id = Guid.Parse("0dab0000-0000-4000-8000-000000000001"),
        Name = "StoreStatistics",
        Live = true,
        DeclaringAssembly = "MintPlayer.Spark.Dashboards",
        LibraryAlias = "dashboards",
        DeclaredName = SparkDashboardsExtensions.DatabaseStatistics,
    };

    private static readonly EntityTypeDefinition AppTypeWithTheOldName = new()
    {
        Id = Guid.Parse("0dab0000-0000-4000-8000-000000000002"),
        Name = SparkDashboardsExtensions.DatabaseStatistics,
        Live = true,
    };

    private static IModelLoader Loader()
    {
        var loader = Substitute.For<IModelLoader>();
        loader.GetEntityTypes().Returns(new[] { Renamed, AppTypeWithTheOldName });
        loader.GetEntityTypeByName(Arg.Any<string>()).Returns(c => new[] { Renamed, AppTypeWithTheOldName }
            .FirstOrDefault(t => string.Equals(t.Name, c.Arg<string>(), StringComparison.OrdinalIgnoreCase)));
        return loader;
    }

    [Fact]
    public void The_library_dashboard_is_its_library_type_under_the_application_name()
    {
        var catalog = new DashboardCatalog();
        var dashboard = catalog.GetOrAdd(SparkDashboardsExtensions.DatabaseStatistics, SparkDashboardsExtensions.LibraryAlias);

        dashboard.TypeOf(Loader()).Should().BeSameAs(Renamed);
        dashboard.DocumentId.Should().Be("SparkDashboards/DatabaseStatistics", "the stored history is keyed by the declared name, which a rename never changes");
    }

    [Fact]
    public void An_application_dashboard_is_the_application_type_of_its_name()
    {
        var catalog = new DashboardCatalog();
        var dashboard = catalog.GetOrAdd(SparkDashboardsExtensions.DatabaseStatistics);

        dashboard.TypeOf(Loader()).Should().BeSameAs(AppTypeWithTheOldName);
    }
}
