using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// <see cref="ProjectedOffsetRestorer"/> driven directly over hand-built projection rows. The
/// RavenDB-backed tests reach only the scalar path; the collection restore and every
/// leave-it-alone guard are pinned here.
/// </summary>
/// <remarks>
/// The row types deliberately carry no <c>[FromIndex]</c>: a projection fixture in this assembly
/// is picked up by assembly-wide index scans elsewhere, which is a known way to break unrelated
/// tests.
/// </remarks>
public class ProjectedOffsetRestorerTests
{
    private static readonly DateTimeOffset Plus2 = new(2026, 3, 9, 10, 0, 0, TimeSpan.FromHours(2));
    private static readonly DateTimeOffset Minus8 = new(2026, 3, 9, 15, 0, 0, TimeSpan.FromHours(-8));

    public sealed class Row
    {
        public DateTimeOffset Starts { get; set; }
        [IgnoreProperty] public SparkIndexValue<DateTimeOffset>? StartsRaw { get; set; }

        public DateTimeOffset? Ends { get; set; }
        [IgnoreProperty] public SparkIndexValue<DateTimeOffset?>? EndsRaw { get; set; }

        public List<DateTimeOffset> Slots { get; set; } = [];
        [IgnoreProperty] public SparkIndexValue<List<DateTimeOffset>>? SlotsRaw { get; set; }

        public DateTimeOffset?[] Maybes { get; set; } = [];
        [IgnoreProperty] public SparkIndexValue<DateTimeOffset?[]>? MaybesRaw { get; set; }
    }

    /// <summary>A domain property that merely happens to be called <c>{Name}Raw</c> must not steer anything.</summary>
    public sealed class NotAWrapper
    {
        public DateTimeOffset Starts { get; set; }
        public SparkIndexValue<DateTimeOffset>? StartsRaw { get; set; }
    }

    /// <summary>A wrapper-named property with no <c>V</c> to carry.</summary>
    public sealed class WrongWrapperShape
    {
        public DateTimeOffset Starts { get; set; }
        [IgnoreProperty] public string? StartsRaw { get; set; }
    }

    public sealed class Unwrapped
    {
        public DateTimeOffset Starts { get; set; }
        public DateTimeOffset ReadOnly => Plus2;
    }

    private static readonly ProjectedOffsetRestorer Restorer = new();

    [Fact]
    public void Scalars_and_nullable_scalars_take_the_wrapped_value()
    {
        var row = new Row
        {
            Starts = Plus2.ToUniversalTime(),
            StartsRaw = new() { V = Plus2 },
            Ends = Minus8.ToUniversalTime(),
            EndsRaw = new() { V = Minus8 },
        };

        Restorer.Restore([row]);

        row.Starts.Offset.Should().Be(TimeSpan.FromHours(2));
        row.Ends!.Value.Offset.Should().Be(TimeSpan.FromHours(-8));
    }

    [Fact]
    public void A_collection_is_restored_element_by_element()
    {
        var row = new Row
        {
            Slots = [Plus2.ToUniversalTime(), Minus8.ToUniversalTime()],
            SlotsRaw = new() { V = [Plus2, Minus8] },
            Maybes = [Plus2.ToUniversalTime(), null],
            MaybesRaw = new() { V = [Plus2, null] },
        };
        var sameList = row.Slots;

        Restorer.Restore([row]);

        row.Slots.Should().BeSameAs(sameList, "restoring assigns into the projected collection");
        row.Slots.Select(s => s.Offset).Should().Equal(TimeSpan.FromHours(2), TimeSpan.FromHours(-8));
        row.Maybes[0]!.Value.Offset.Should().Be(TimeSpan.FromHours(2));
        row.Maybes[1].HasValue.Should().BeFalse();
    }

    [Fact]
    public void A_collection_whose_count_disagrees_is_left_alone()
    {
        // Restoring a prefix would leave the row internally inconsistent.
        var utc = Plus2.ToUniversalTime();
        var row = new Row { Slots = [utc], SlotsRaw = new() { V = [Plus2, Minus8] } };

        Restorer.Restore([row]);

        row.Slots.Should().Equal(utc);
        row.Slots[0].Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void A_missing_wrapper_or_a_missing_value_leaves_the_projection_as_it_was()
    {
        // The wrapper is genuinely absent while RavenDB rebuilds an index after a deploy.
        var utc = Plus2.ToUniversalTime();
        var noWrapper = new Row { Starts = utc };
        var nullValue = new Row { Starts = utc, SlotsRaw = new() { V = null! }, Slots = [utc] };

        Restorer.Restore([noWrapper, null!, nullValue]);

        noWrapper.Starts.Should().Be(utc);
        noWrapper.Starts.Offset.Should().Be(TimeSpan.Zero);
        nullValue.Slots[0].Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void A_property_not_marked_IgnoreProperty_is_not_a_wrapper()
    {
        var utc = Plus2.ToUniversalTime();
        var row = new NotAWrapper { Starts = utc, StartsRaw = new() { V = Plus2 } };

        Restorer.Restore([row]);

        row.Starts.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void A_wrapper_with_nothing_to_carry_is_ignored()
    {
        var utc = Plus2.ToUniversalTime();
        var row = new WrongWrapperShape { Starts = utc, StartsRaw = "not a wrapper" };

        Restorer.Restore([row]);

        row.Starts.Should().Be(utc);
    }

    [Fact]
    public void A_row_type_with_no_wrappers_and_an_empty_batch_are_both_no_ops()
    {
        var row = new Unwrapped { Starts = Plus2 };

        Restorer.Restore([]);
        Restorer.Restore([row, row]);

        row.Starts.Should().Be(Plus2);
    }
}
