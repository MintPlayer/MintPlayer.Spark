using System.Globalization;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Client.Authorization;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Mapper;

/// <summary>
/// The `DateTimeOffset` fidelity work, end to end over HTTP against the real Fleet API.
/// <para>
/// The existing coverage sits either side of the wire: <c>DateTimeOffsetRoundTripTests</c> in
/// `MintPlayer.Spark.Tests` drives RavenDB through <c>SparkTestDriver</c>, and the ng-spark specs
/// cover the browser conversions. Neither crosses the HTTP boundary, and both defects this work
/// found lived on paths that no single layer's tests could see.
/// </para>
/// <para>
/// ⚠️ Every assertion here compares the <b>offset</b> or uses <c>EqualsExact</c>.
/// <c>DateTimeOffset.Equals</c> — and therefore <c>Should().Be(...)</c> — compares the *instant*, so
/// an ordinary equality assertion passes against the exact value this whole change exists to fix.
/// That is why a 2000-test suite sailed over the defect for years.
/// </para>
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class DateTimeOffsetRoundTripTests
{
    private const string RegisteredAt = "RegisteredAt";

    /// A deliberately hostile value: an offset nobody's server runs in, on a date whose wall clock
    /// falls on a *different day* from its UTC instant. If anything along the path normalises to UTC,
    /// both the offset and the date change.
    private static readonly DateTimeOffset Seattle =
        new(2026, 12, 31, 23, 59, 0, TimeSpan.FromHours(-8));

    private readonly FleetE2ECollectionFixture _fixture;
    public DateTimeOffsetRoundTripTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Create_then_fetch_preserves_the_offset_over_the_wire()
    {
        using var client = await SparkClientFactory.ForFleetAsAdminAsync(_fixture.Host);

        var created = await client.CreatePersistentObjectAsync(NewCarRegisteredAt(Seattle));
        created.Id.Should().NotBeNullOrEmpty(
            $"car create must return id\n--- Fleet log tail ---\n{_fixture.Host.RecentLog()}");

        var refetched = await client.GetPersistentObjectAsync(CarFixture.TypeId, created.Id!)
            ?? throw new InvalidOperationException($"Created car {created.Id} not re-fetchable");

        var value = ParseRegisteredAt(refetched);

        value.Offset.Should().Be(TimeSpan.FromHours(-8), "the originating offset must survive the round trip");
        value.EqualsExact(Seattle).Should().BeTrue($"expected {Seattle:o} exactly, got {value:o}");
        value.DateTime.Day.Should().Be(31, "the stored WALL CLOCK is 31 December, even though the instant is 1 January");
    }

    [Fact]
    public async Task An_edit_is_actually_persisted()
    {
        // The write path used to do nothing at all while reporting success: Convert.ChangeType threw
        // on DateTimeOffset and a bare catch swallowed it, so the save returned 200 and discarded the
        // value. Nothing downstream could detect that, because the old value came back intact.
        using var client = await SparkClientFactory.ForFleetAsAdminAsync(_fixture.Host);

        var created = await client.CreatePersistentObjectAsync(NewCarRegisteredAt(Seattle));
        var refetched = await client.GetPersistentObjectAsync(CarFixture.TypeId, created.Id!)
            ?? throw new InvalidOperationException($"Created car {created.Id} not re-fetchable");

        var edited = new DateTimeOffset(2026, 7, 4, 9, 30, 0, TimeSpan.FromHours(5.75)); // +05:45, Kathmandu
        await client.UpdatePersistentObjectAsync(WithRegisteredAt(refetched, edited));

        var afterUpdate = await client.GetPersistentObjectAsync(CarFixture.TypeId, created.Id!)
            ?? throw new InvalidOperationException($"Updated car {created.Id} not re-fetchable");

        var value = ParseRegisteredAt(afterUpdate);

        value.EqualsExact(edited).Should().BeTrue($"the edit must persist exactly; expected {edited:o}, got {value:o}");
        value.Offset.Should().Be(TimeSpan.FromMinutes(345), "a 45-minute offset must survive, not just whole hours");
    }

    [Fact]
    public async Task The_index_backed_query_agrees_with_the_document()
    {
        // The headline defect. A grid is answered from the index's stored fields and a detail page
        // from the document, and RavenDB flattens a DateTimeOffset to its UTC equivalent the moment
        // it becomes a scalar index field -- so the two disagreed, visibly, in the app. The generated
        // {Name}Raw wrapper is what makes them agree again.
        using var client = await SparkClientFactory.ForFleetAsAdminAsync(_fixture.Host);

        var plate = CarFixture.RandomLicensePlate("DTO");
        var created = await client.CreatePersistentObjectAsync(NewCarRegisteredAt(Seattle, plate));

        var fromDocument = ParseRegisteredAt(
            await client.GetPersistentObjectAsync(CarFixture.TypeId, created.Id!)
            ?? throw new InvalidOperationException("car not re-fetchable"));

        // The query path reads an index, and indexes are eventually consistent -- without this the row
        // is simply absent for a moment after the write, which looks like a broken query rather than a
        // timing problem.
        await _fixture.Host.WaitForIndexingAsync();

        var fromIndex = await FindInQueryAsync(client, plate);

        fromIndex.Offset.Should().Be(TimeSpan.FromHours(-8),
            "the query path reads the index, which is where the offset used to be destroyed");
        fromIndex.EqualsExact(fromDocument).Should().BeTrue(
            $"grid and detail must name the same moment AND the same wall clock; "
            + $"index={fromIndex:o} document={fromDocument:o}");
    }

    /// <summary>
    /// Three registrations whose instant order is the REVERSE of their wall-clock order: car 0 has the
    /// latest clock and the earliest instant. Ordering by text or by local clock gives 2, 1, 0.
    /// </summary>
    private static readonly DateTimeOffset[] InstantAscending =
    [
        new(2027, 3, 2, 01, 00, 0, TimeSpan.FromHours(12)),   // 2027-03-01T13:00Z
        new(2027, 3, 1, 09, 00, 0, TimeSpan.FromHours(-5)),   // 2027-03-01T14:00Z
        new(2027, 3, 1, 16, 00, 0, TimeSpan.FromHours(1)),    // 2027-03-01T15:00Z
    ];

    [Fact]
    public async Task Sorting_across_mixed_offsets_is_chronological_by_instant()
    {
        // This test used to fetch each car by its own plate search, in seed order, and assert the seed
        // was ascending — it never asked the server to sort (PRD §1). Now the server sorts, and the
        // order of one result page is what is asserted.
        using var client = await SparkClientFactory.ForFleetAsAdminAsync(_fixture.Host);
        var prefix = UniquePrefix();

        foreach (var (value, i) in InstantAscending.Select((v, i) => (v, i)))
            await client.CreatePersistentObjectAsync(NewCarRegisteredAt(value, $"{prefix}{i}"));
        await _fixture.Host.WaitForIndexingAsync();

        var ascending = await client.ExecuteQueryAsync("registrations", search: prefix,
            sortColumns: [new SortColumn { Property = RegisteredAt, Direction = "asc" }]);
        var descending = await client.ExecuteQueryAsync("registrations", search: prefix,
            sortColumns: [new SortColumn { Property = RegisteredAt, Direction = "desc" }]);

        Plates(ascending).Should().Equal($"{prefix}0", $"{prefix}1", $"{prefix}2");
        Plates(descending).Should().Equal($"{prefix}2", $"{prefix}1", $"{prefix}0");

        // The guard that makes the assertion above mean something: by wall clock the order differs.
        var clocks = InstantAscending.Select(v => v.DateTime).ToArray();
        clocks.Zip(clocks.Skip(1), (a, b) => a <= b).All(x => x).Should().BeFalse(
            "the cars must be ordered differently by wall clock than by instant, or this proves nothing");
    }

    [Fact]
    public async Task A_column_filter_selects_by_instant_over_http()
    {
        using var client = await SparkClientFactory.ForFleetAsAdminAsync(_fixture.Host);
        var prefix = UniquePrefix();

        foreach (var (value, i) in InstantAscending.Select((v, i) => (v, i)))
            await client.CreatePersistentObjectAsync(NewCarRegisteredAt(value, $"{prefix}{i}"));
        await _fixture.Host.WaitForIndexingAsync();

        // Car 0's instant (13:00Z) written in an offset none of the cars uses.
        var result = await client.ExecuteQueryAsync("registrations", search: prefix,
            columns: [new QueryColumnFilter { Name = RegisteredAt, Includes = ["2027-03-01T08:00:00-05:00"] }]);

        Plates(result).Should().Equal($"{prefix}0");
    }

    [Fact]
    public async Task Several_sort_columns_order_within_each_group_by_instant()
    {
        // The grid sends this on shift-click: one sort column per header, in click order.
        using var client = await SparkClientFactory.ForFleetAsAdminAsync(_fixture.Host);
        var prefix = UniquePrefix();

        // Group "X" holds cars 0 and 2, group "Y" car 1 and a fourth, earlier car.
        await client.CreatePersistentObjectAsync(NewCarRegisteredAt(InstantAscending[0], $"{prefix}0", $"{prefix}X"));
        await client.CreatePersistentObjectAsync(NewCarRegisteredAt(InstantAscending[1], $"{prefix}1", $"{prefix}Y"));
        await client.CreatePersistentObjectAsync(NewCarRegisteredAt(InstantAscending[2], $"{prefix}2", $"{prefix}X"));
        await client.CreatePersistentObjectAsync(NewCarRegisteredAt(new(2026, 12, 31, 23, 59, 0, TimeSpan.FromHours(-8)), $"{prefix}3", $"{prefix}Y"));
        await _fixture.Host.WaitForIndexingAsync();

        var newestFirstPerModel = await client.ExecuteQueryAsync("registrations", search: prefix,
            sortColumns:
            [
                new SortColumn { Property = CarFixture.AttributeNames.Model, Direction = "asc" },
                new SortColumn { Property = RegisteredAt, Direction = "desc" },
            ]);

        Plates(newestFirstPerModel).Should().Equal($"{prefix}2", $"{prefix}0", $"{prefix}1", $"{prefix}3");
    }

    // ---------------------------------------------------------------------------------

    /// <summary>
    /// Five characters that start every plate (and model) a test creates, so a search for them returns
    /// that test's cars and nothing else in the shared Fleet database.
    /// </summary>
    private static string UniquePrefix() => CarFixture.RandomLicensePlate("Q")[..6];

    private static string[] Plates(QueryResult result)
        => result.Items
            .Select(i => i.Values.FirstOrDefault(v => v.Key == "LicensePlate")?.Value?.ToString() ?? "")
            .ToArray();

    private static PersistentObject NewCarRegisteredAt(DateTimeOffset value, string? plate = null, string? model = null)
    {
        var po = model is null
            ? CarFixture.New(plate ?? CarFixture.RandomLicensePlate("DTO"))
            : CarFixture.New(plate ?? CarFixture.RandomLicensePlate("DTO"), model);
        return new PersistentObject
        {
            Name = po.Name,
            ObjectTypeId = po.ObjectTypeId,
            Attributes =
            [
                .. po.Attributes,
                // Round-trip format ("o"), which is what the browser sends: a complete ISO-8601
                // string with the offset already applied. The server never infers one.
                new PersistentObjectAttribute { Name = RegisteredAt, Value = value.ToString("o", CultureInfo.InvariantCulture) },
            ],
        };
    }

    private static PersistentObject WithRegisteredAt(PersistentObject existing, DateTimeOffset value)
        => new()
        {
            Id = existing.Id,
            Etag = existing.Etag,
            Name = existing.Name,
            ObjectTypeId = existing.ObjectTypeId,
            Attributes = [.. existing.Attributes.Select(a => a.Name == RegisteredAt
                ? new PersistentObjectAttribute { Name = a.Name, Value = value.ToString("o", CultureInfo.InvariantCulture), IsValueChanged = true }
                : a)],
        };

    private static DateTimeOffset ParseRegisteredAt(PersistentObject po)
    {
        var raw = po.Attributes.Single(a => a.Name == RegisteredAt).Value?.ToString();
        raw.Should().NotBeNullOrEmpty($"{RegisteredAt} must be present on the wire");
        return DateTimeOffset.Parse(raw!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    /// Reads the value back through the index-backed query path, which is the one that used to lose
    /// the offset. Searches by plate so the row is found regardless of paging.
    private static async Task<DateTimeOffset> FindInQueryAsync(SparkClient client, string plate)
    {
        var result = await client.ExecuteQueryAsync("registrations", skip: 0, take: 10, search: plate);

        var row = result.Items.SingleOrDefault(i =>
            i.Values.FirstOrDefault(v => v.Key == "LicensePlate")?.Value?.ToString() == plate)
            ?? throw new InvalidOperationException($"car {plate} not found through the query path");

        var value = row.Values.FirstOrDefault(v => v.Key == RegisteredAt)?.Value;
        value.Should().NotBeNull($"{RegisteredAt} must be projected by the query");

        return DateTimeOffset.Parse(value!.ToString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}
