using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests;

/// <summary>
/// Defect D: <see cref="DateTimeOffset"/> writes are silently dropped.
/// <para>
/// <c>EntityMapper.SetPropertyValue</c> branches on <c>string</c>, <c>Guid</c>, <c>DateTime</c>,
/// <c>DateOnly</c>, <c>Color</c> and enums — but not <c>DateTimeOffset</c>. A wire value arrives as a
/// string and falls through to <c>Convert.ChangeType</c>, which throws
/// <c>InvalidCastException: Invalid cast from 'System.String' to 'System.DateTimeOffset'</c> because
/// <c>DateTimeOffset</c> does not implement <c>IConvertible</c> (<c>DateTime</c> does). A bare
/// <c>catch</c> swallows it, so the save reports success and the value is unchanged.
/// </para>
/// <para>
/// The behaviour is asymmetric: the null branch sits <em>before</em> the <c>try</c>, so
/// <strong>clearing</strong> a nullable value has always worked while <strong>setting</strong> one
/// silently no-ops.
/// </para>
/// <para>
/// Contract (decided): the client sends a full ISO-8601 string carrying the offset, and the server
/// never infers one. A value that arrives without an offset is taken as <c>+00:00</c> — Spark
/// preserves offsets it is given and does not invent them.
/// </para>
/// </summary>
public class EntityMapperDateTimeOffsetWriteTests
{
    private static readonly Guid MeetingTypeId = Guid.Parse("eeee5555-eeee-eeee-eeee-eeee55555555");

    private readonly IModelLoader _modelLoader = Substitute.For<IModelLoader>();
    private readonly EntityMapper _mapper;

    public EntityMapperDateTimeOffsetWriteTests()
    {
        var guard = Substitute.For<ICollectionGuard>();
        guard.BelongsToAuthorizedCollection(default!, default!, default!).ReturnsForAnyArgs(true);
        _mapper = new EntityMapper(_modelLoader, collectionGuard: guard);
    }

    private sealed class Meeting
    {
        public string? Id { get; set; }
        public DateTimeOffset Starts { get; set; }
        public DateTimeOffset? MaybeEnds { get; set; }
    }

    private static PersistentObject PoWith(params (string Name, object? Value)[] attrs)
        => new()
        {
            Name = "Meeting",
            ObjectTypeId = MeetingTypeId,
            Attributes = attrs.Select(a => new PersistentObjectAttribute
            {
                Name = a.Name,
                Value = a.Value,
                DataType = "datetime",
            }).ToArray(),
        };

    [Fact]
    public void Setting_a_DateTimeOffset_from_an_iso_string_persists_the_value()
    {
        var meeting = new Meeting();

        _mapper.PopulateObjectValues(PoWith(("Starts", "2026-03-09T10:00:00.0000000+02:00")), meeting);

        meeting.Starts.Should().Be(new DateTimeOffset(2026, 3, 9, 10, 0, 0, TimeSpan.FromHours(2)));
    }

    [Fact]
    public void Setting_a_DateTimeOffset_preserves_a_negative_offset()
    {
        var meeting = new Meeting();

        _mapper.PopulateObjectValues(PoWith(("Starts", "2026-03-09T15:00:00.0000000-08:00")), meeting);

        meeting.Starts.Offset.Should().Be(TimeSpan.FromHours(-8));
        meeting.Starts.Hour.Should().Be(15, "the wall clock the client sent must survive, not be shifted to UTC");
    }

    [Fact]
    public void Setting_a_DateTimeOffset_preserves_a_fractional_offset()
    {
        var meeting = new Meeting();

        _mapper.PopulateObjectValues(PoWith(("Starts", "2026-03-09T10:00:00.0000000+05:45")), meeting);

        meeting.Starts.Offset.Should().Be(new TimeSpan(5, 45, 0));
    }

    [Fact]
    public void An_offsetless_value_is_taken_as_utc()
    {
        var meeting = new Meeting();

        _mapper.PopulateObjectValues(PoWith(("Starts", "2026-03-09T10:00:00.0000000")), meeting);

        meeting.Starts.Offset.Should().Be(TimeSpan.Zero,
            "Spark preserves offsets it is given and does not invent them; an absent offset means UTC");
        meeting.Starts.Hour.Should().Be(10);
    }

    [Fact]
    public void Setting_a_nullable_DateTimeOffset_persists_the_value()
    {
        var meeting = new Meeting();

        _mapper.PopulateObjectValues(PoWith(("MaybeEnds", "2026-03-09T10:00:00.0000000+02:00")), meeting);

        meeting.MaybeEnds.Should().Be(new DateTimeOffset(2026, 3, 9, 10, 0, 0, TimeSpan.FromHours(2)));
    }

    [Fact]
    public void An_already_typed_DateTimeOffset_is_assigned_directly()
    {
        var meeting = new Meeting();
        var value = new DateTimeOffset(2026, 3, 9, 10, 0, 0, TimeSpan.FromHours(2));

        _mapper.PopulateObjectValues(PoWith(("Starts", value)), meeting);

        meeting.Starts.Should().Be(value);
    }

    /// <summary>
    /// The half that has always worked, pinned so the fix does not regress it: the null branch runs
    /// before the <c>try</c>, so clearing never hit the swallowed cast.
    /// </summary>
    [Fact]
    public void Clearing_a_nullable_DateTimeOffset_still_works()
    {
        var meeting = new Meeting { MaybeEnds = DateTimeOffset.UtcNow };

        _mapper.PopulateObjectValues(PoWith(("MaybeEnds", null)), meeting);

        meeting.MaybeEnds.HasValue.Should().BeFalse();
    }
}
