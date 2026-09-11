using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// The coverage whose absence let a data-fidelity defect live for years: nothing in the suite ever
/// stored a <see cref="DateTimeOffset"/> and asserted on the value read back.
/// <para>
/// RavenDB converts a <c>DateTimeOffset</c> to its UTC-equivalent <c>DateTime</c> whenever the value
/// becomes a <strong>scalar index field</strong>, forcing the offset to <c>+00:00</c>. The document
/// keeps the offset; a projection served from a stored index field does not. Upstream:
/// <see href="https://github.com/ravendb/ravendb/issues/17901"/>, open since 2023-12, both engines.
/// </para>
/// <para>
/// The shift equals <em>each document's own offset</em>, not the machine's timezone — a
/// <c>-08:00</c> value comes back eight hours <em>later</em>. Every fixture value below therefore
/// carries a deliberate non-UTC offset, including a negative one: a UTC-only corpus passes a wrong
/// implementation, and a CI box in UTC would never notice. The offsets are pinned in the data
/// rather than by touching the process timezone.
/// </para>
/// <para>
/// A value nested inside a complex object is stored as an opaque sub-document and never decomposed,
/// so it survives intact — that is the mechanism the fix rests on, and it is pinned here too.
/// </para>
/// </summary>
public class DateTimeOffsetRoundTripTests : SparkTestDriver
{
    private static readonly Guid MeetingTypeId = Guid.Parse("dddd4444-dddd-dddd-dddd-dddd44444444");

    /// <summary>Brussels summer time — the offset that produced the original "~2 hours" report.</summary>
    private static readonly DateTimeOffset Positive = new(2026, 3, 9, 10, 0, 0, TimeSpan.FromHours(2));

    /// <summary>Negative offset: comes back <em>later</em>, which disproves "always minus two hours".</summary>
    private static readonly DateTimeOffset Negative = new(2026, 3, 9, 15, 0, 0, TimeSpan.FromHours(-8));

    /// <summary>Control: a zero offset round-trips correctly even today.</summary>
    private static readonly DateTimeOffset Utc = new(2026, 3, 9, 12, 0, 0, TimeSpan.Zero);

    public class Meeting
    {
        public string? Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public DateTimeOffset Starts { get; set; }
        public DateTimeOffset? MaybeEnds { get; set; }
    }

    /// <summary>
    /// Mirrors what the generator emits today: the scalar field plus the blanket
    /// <c>StoreAllFields</c> that makes a projection read from the index rather than the document.
    /// </summary>
    public class Meetings_Overview : AbstractIndexCreationTask<Meeting>
    {
        public Meetings_Overview()
        {
            Map = meetings => from meeting in meetings
                              select new
                              {
                                  meeting.Title,
                                  meeting.Starts,
                                  meeting.MaybeEnds,
                              };
            StoreAllFields(FieldStorage.Yes);
        }
    }

    [FromIndex(typeof(Meetings_Overview))]
    public class VMeeting
    {
        public string? Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public DateTimeOffset Starts { get; set; }
        public DateTimeOffset? MaybeEnds { get; set; }
    }

    public class TestContext : SparkContext
    {
        public IRavenQueryable<Meeting> Meetings => Session.Query<Meeting>();
    }

    private async Task SeedAsync()
    {
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(new Meeting { Title = "Positive", Starts = Positive, MaybeEnds = Positive }, "meetings/1");
        await session.StoreAsync(new Meeting { Title = "Negative", Starts = Negative, MaybeEnds = null }, "meetings/2");
        await session.StoreAsync(new Meeting { Title = "Utc", Starts = Utc, MaybeEnds = Utc }, "meetings/3");
        await session.SaveChangesAsync();

        await new Meetings_Overview().ExecuteAsync(Store);
        await RavenIndexHelper.WaitForNonStaleAsync(Store);
    }

    // --- RavenDB behaviour: these pin the defect itself -------------------------------------
    // They pass today and must keep passing. If a future RavenDB fixes #17901 upstream, they fail
    // loudly rather than leaving the framework carrying dead reconstruction code.

    [Fact]
    public async Task Session_load_preserves_the_offset()
    {
        await SeedAsync();

        using var session = Store.OpenAsyncSession();
        var loaded = await session.LoadAsync<Meeting>("meetings/1");

        loaded.Starts.Offset.Should().Be(TimeSpan.FromHours(2));
        loaded.Starts.Should().Be(Positive);
    }

    [Fact]
    public async Task Projecting_a_scalar_field_from_a_stored_index_destroys_the_offset()
    {
        await SeedAsync();

        using var session = Store.OpenAsyncSession();
        var projected = await session.Query<VMeeting, Meetings_Overview>()
            .Where(v => v.Title == "Positive")
            .ProjectInto<VMeeting>()
            .FirstAsync();

        projected.Starts.Offset.Should().Be(TimeSpan.Zero,
            "RavenDB normalises a scalar DateTimeOffset index field to its UTC equivalent");
        projected.Starts.UtcTicks.Should().Be(Positive.UtcTicks,
            "the instant survives — only the offset and the wall-clock reading are lost, which is why " +
            "ordering and filtering were never affected and nobody noticed");
    }

    [Fact]
    public async Task The_shift_follows_each_documents_own_offset_not_the_machine_timezone()
    {
        await SeedAsync();

        using var session = Store.OpenAsyncSession();
        var negative = await session.Query<VMeeting, Meetings_Overview>()
            .Where(v => v.Title == "Negative")
            .ProjectInto<VMeeting>()
            .FirstAsync();

        // -08:00 stored as 15:00 comes back as 23:00Z — LATER, not earlier.
        negative.Starts.Hour.Should().Be(23);
        negative.Starts.UtcTicks.Should().Be(Negative.UtcTicks);
    }

    // --- Spark pipeline: these FAIL today and are what the fix repairs ------------------------

    // ⚠️ These assert with EqualsExact, never with Should().Be(...).
    //
    // DateTimeOffset equality compares the INSTANT, so `10:00+02:00 == 08:00+00:00` is true. The
    // defect preserves the instant and destroys only the offset, so an ordinary equality assertion
    // passes against the broken value and would go on passing against a broken fix. This was
    // observed, not theorised: the first draft of these tests passed while the pipeline was
    // provably returning 08:00+00:00.

    [Fact]
    public async Task QueryExecutor_returns_the_original_offset()
    {
        await SeedAsync();

        var rows = await QueryMeetingsAsync();
        var actual = (DateTimeOffset)RowValue(rows, "Positive", "Starts")!;

        actual.Offset.Should().Be(TimeSpan.FromHours(2));
        actual.Hour.Should().Be(10, "the stored wall clock must survive, not be re-expressed in UTC");
        actual.EqualsExact(Positive).Should().BeTrue();
    }

    [Fact]
    public async Task QueryExecutor_returns_the_original_offset_for_a_negative_offset()
    {
        await SeedAsync();

        var rows = await QueryMeetingsAsync();
        var actual = (DateTimeOffset)RowValue(rows, "Negative", "Starts")!;

        actual.Offset.Should().Be(TimeSpan.FromHours(-8));
        actual.Hour.Should().Be(15);
        actual.EqualsExact(Negative).Should().BeTrue();
    }

    [Fact]
    public async Task QueryExecutor_leaves_a_zero_offset_value_untouched()
    {
        await SeedAsync();

        var rows = await QueryMeetingsAsync();
        var actual = (DateTimeOffset)RowValue(rows, "Utc", "Starts")!;

        actual.EqualsExact(Utc).Should().BeTrue(
            "the control: a zero-offset value is correct today and must not be disturbed by the fix");
    }

    [Fact]
    public async Task QueryExecutor_round_trips_a_nullable_offset_and_its_null()
    {
        await SeedAsync();

        var rows = await QueryMeetingsAsync();

        ((DateTimeOffset)RowValue(rows, "Positive", "MaybeEnds")!).EqualsExact(Positive).Should().BeTrue();
        RowValue(rows, "Negative", "MaybeEnds").Should().BeNull(
            "a null must stay null — never be reconstructed as +00:00, which would be a wrong value " +
            "rather than a missing one");
    }

    private async Task<List<Abstractions.QueryResultItem>> QueryMeetingsAsync()
    {
        await using var factory = new SparkEndpointFactory<TestContext>(Store, [MeetingModel()],
            configureIndexCatalog: catalog =>
            {
                catalog.RegisterIndex(typeof(Meetings_Overview));
                catalog.RegisterProjection(typeof(VMeeting), typeof(Meetings_Overview));
            });

        var executor = factory.GetService<IQueryExecutor>();
        var result = await executor.ExecuteQueryAsync(new SparkQuery
        {
            Id = Guid.NewGuid(),
            Name = "Meetings",
            Source = "Database.Meetings",
            IndexName = "Meetings_Overview",
        });

        return result.Items.ToList();
    }

    private static object? RowValue(List<Abstractions.QueryResultItem> rows, string title, string attribute)
        => rows.Single(row => row.Values.Single(a => a.Key == "Title").Value?.ToString() == title)
               .Values.Single(a => a.Key == attribute).Value;

    private static EntityTypeFile MeetingModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = MeetingTypeId,
            Name = "Meeting",
            ClrType = typeof(Meeting).FullName!,
            Breadcrumb = "{Title}",
            Attributes = [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Title", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Starts", DataType = "datetime" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "MaybeEnds", DataType = "datetime" },
            ],
        },
    };
}
