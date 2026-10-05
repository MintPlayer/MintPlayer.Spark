using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using static MintPlayer.Spark.Tests.Services.RegCarQuerySortFilterTests;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// A filter value written <b>without an offset</b> means the same instant as it does when it is saved:
/// UTC (PRD F2, D2). Before the fix the filter path read it as server-local while the write path read
/// it as UTC, so the same string named two different instants depending on where the server ran.
/// </summary>
/// <remarks>
/// The defect only shows when the server is not in UTC, and CI is. The class therefore runs under a
/// simulated Brussels zone (<see cref="LocalZone"/>, PRD D11), in a collection nothing runs alongside,
/// with its own host built inside that collection.
/// </remarks>
[Collection(LocalZoneCollection.Name)]
public class RegCarLocalZoneFilterTests(SeededRegCars host)
    : SparkSharedTestDriver(host), IClassFixture<SeededRegCars>, IDisposable
{
    private readonly LocalZone _zone = new();
    private IServiceScope? _scope;

    public void Dispose()
    {
        _scope?.Dispose();
        _zone.Dispose();
    }

    private async Task<QueryResult> FilterAsync(string json)
    {
        _scope = host.Factory.CreateScope();
        var executor = _scope.ServiceProvider.GetRequiredService<IQueryExecutor>();

        return await executor.ExecuteQueryAsync(Registrations(Asc(nameof(RegCar.LicensePlate))),
            columnFilters: [new QueryColumnFilter { Name = nameof(RegCar.RegisteredAt), Includes = [Wire(json)] }]);
    }

    [Fact]
    public async Task T15_an_offset_less_value_is_the_utc_instant_as_on_save()
    {
        LocalZone.RequireNonUtc(new DateTime(2027, 3, 1, 15, 0, 0, DateTimeKind.Utc));

        // 15:00 with no offset is 15:00Z, the instant C and D share. Read as Brussels time it would be
        // 14:00Z, which is B.
        var result = await FilterAsync("\"2027-03-01T15:00:00\"");

        Plates(result).Should().Equal("C", "D");
    }

    [Fact]
    public async Task T16_a_date_only_value_is_utc_midnight_and_matches_only_that_instant()
    {
        LocalZone.RequireNonUtc(new DateTime(2027, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        // There is no "on this day" filter: a date is the instant 00:00Z of that day. G is registered
        // at exactly 2027-02-01T00:00Z; read as Brussels midnight it would be 2027-01-31T23:00Z.
        var onG = await FilterAsync("\"2027-02-01\"");
        var onAnotherDay = await FilterAsync("\"2027-03-01\"");

        Plates(onG).Should().Equal("G");
        onAnotherDay.TotalItems.Should().Be(0, "no row is registered at 2027-03-01T00:00Z, whatever happened later that day");
    }
}
