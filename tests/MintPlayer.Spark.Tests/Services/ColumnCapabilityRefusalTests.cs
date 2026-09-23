using MintPlayer.Assertions;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// The capabilities a column's <em>shape</em> refuses, whatever the model says.
/// </summary>
/// <remarks>
/// ⚠️ These went in with no test at first and the whole suite stayed green, because nothing else
/// exercises <see cref="ColumnCapabilities"/> against an array or an embedded attribute at all. A
/// green run said nothing about them — which is the same "a clean grep is not evidence" trap that has
/// bitten this repo before.
/// <para>
/// The refusals are structural rather than defaults, so a model author cannot opt back into them. That
/// is deliberate: each one is a wrong answer the framework would otherwise give silently, not a
/// presentation choice.
/// </para>
/// </remarks>
public class ColumnCapabilityRefusalTests
{
    private static EntityAttributeDefinition Attribute(
        string dataType = "string", bool isArray = false,
        bool? canSort = null, bool? canFilter = null, bool? canListDistincts = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = "Probe",
            DataType = dataType,
            IsArray = isArray,
            CanSort = canSort,
            CanFilter = canFilter,
            CanListDistincts = canListDistincts,
        };

    [Fact]
    public void A_plain_scalar_is_capable_by_default()
    {
        var attribute = Attribute();

        ColumnCapabilities.CanSort(attribute, null).Should().BeTrue();
        ColumnCapabilities.CanFilter(attribute, null).Should().BeTrue();
        ColumnCapabilities.CanListDistincts(attribute, null).Should().BeTrue();
    }

    [Fact]
    public void An_explicit_false_still_wins_on_a_scalar()
    {
        ColumnCapabilities.CanSort(Attribute(canSort: false), null).Should().BeFalse(
            "the structural refusals must not have swallowed the model's own answer");
    }

    /// <summary>
    /// ⚠️ Ordering by a collection does not merely give an arbitrary order — it <b>drops the rows whose
    /// collection is empty</b>, and returns byte-identical output for ascending and descending.
    /// Measured on RavenDB 7.2.6 against nullable-scalar controls that behaved correctly in the same
    /// run, so it is a property of collection-ness rather than of a missing term.
    /// </summary>
    [Fact]
    public void A_collection_column_can_never_be_sorted_even_when_the_model_says_it_can()
    {
        ColumnCapabilities.CanSort(Attribute(isArray: true, canSort: true), null).Should().BeFalse(
            "a result set that changes size because the user clicked a column header is not something " +
            "a model author can consent to");
    }

    /// <summary>
    /// The refusal must be narrow. Filtering a collection <em>works</em> — it emits
    /// <c>coll.Any(e => e == v)</c>, which RavenDB serves from multi-valued terms.
    /// </summary>
    [Fact]
    public void A_collection_column_can_still_be_filtered()
    {
        var attribute = Attribute(isArray: true);

        ColumnCapabilities.CanFilter(attribute, null).Should().BeTrue(
            "collection filtering is correct since AnyContains; refusing it here would be over-reach");
    }

    /// <summary>
    /// An embedded object has no scalar term in the index and no scalar value on the wire —
    /// <c>EntityMapper</c> nulls the attribute's value and carries the detail separately. So a filter
    /// panel collapses to one <c>&lt; none &gt;</c> bucket, and the sort is a silent no-op because the
    /// generator emits it at <c>FieldIndexing.No</c>.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void An_embedded_column_refuses_all_three(bool? authored)
    {
        var attribute = Attribute("AsDetail", canSort: authored, canFilter: authored, canListDistincts: authored);

        ColumnCapabilities.CanSort(attribute, null).Should().BeFalse();
        ColumnCapabilities.CanFilter(attribute, null).Should().BeFalse();
        ColumnCapabilities.CanListDistincts(attribute, null).Should().BeFalse(
            "six production CodeCoverage columns drew a funnel whose panel held a single null bucket");
    }

    [Fact]
    public void The_embedded_test_is_matching_the_data_type_the_synchronizer_actually_writes()
    {
        // Guards the string literal: if the synchronizer ever renames this dataType, the refusals above
        // would pass vacuously against a value nothing produces.
        ColumnCapabilities.CanFilter(Attribute("AsDetail"), null).Should().BeFalse();
        ColumnCapabilities.CanFilter(Attribute("asdetail"), null).Should().BeFalse(
            "the comparison is case-insensitive, matching how the rest of the model is read");
    }
}
