using CodeCoverage.Tests._Infrastructure;
using Raven.Client.Documents.Operations.Indexes;
using Xunit;

namespace CodeCoverage.Tests.Controllers;

/// <summary>
/// Indexes belong to the application and are static: the app must never make RavenDB build an auto index.
/// </summary>
/// <remarks>
/// An auto index is built on the first query that needs one, in production on the live database, and every write to
/// its collection then waits on it (D-O18). This boots the whole app on a fresh database, so every migration, startup
/// gate and hosted service runs, and asserts that none of them left an <c>Auto/</c> index behind.
/// </remarks>
[Collection(CoverageWebHostCollection.Name)]
public class NoAutoIndexEndToEndTests : CoverageRavenTest
{
    [Fact]
    public async Task Starting_the_app_on_a_fresh_database_creates_no_auto_index()
    {
        using var store = GetDocumentStore();
        await using (var factory = new CoverageWebAppFactory(store))
        {
            _ = factory.CreateClient();
            WaitForIndexing(store);
        }

        var names = await store.Maintenance.SendAsync(new GetIndexNamesOperation(0, 1024));
        names.Where(name => name.StartsWith("Auto/", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty("indexes belong to the application, and an auto index is built on the live database by whatever query first needs it");
    }
}
