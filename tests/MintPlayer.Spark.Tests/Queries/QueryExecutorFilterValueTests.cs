using System.Reflection;
using System.Text.Json;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Tests.Queries;

/// <summary>
/// <c>QueryExecutor.TryConvertFilterValue</c>, the gate between a caller's filter value and the typed
/// comparison it becomes, and <see cref="RowSortComparer"/>, the in-memory ordering.
/// </summary>
/// <remarks>
/// The filter tests elsewhere pass CLR values, but the wire hands the executor a
/// <see cref="JsonElement"/> — so the branch real requests take had never run. A malformed value
/// must answer <see langword="false"/> (the row set narrows to nothing) rather than throw, and must
/// not quietly turn into a request for null. Reached by reflection: the method is private and its
/// callers need a RavenDB query to get there.
/// </remarks>
public class QueryExecutorFilterValueTests
{
    public enum Level { Low, High }

    private static readonly MethodInfo TryConvert = typeof(QueryExecutor).GetMethod(
        "TryConvertFilterValue", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("QueryExecutor.TryConvertFilterValue was renamed or removed.");

    private static (bool Ok, object? Value) Convert(object? raw, Type propertyType)
    {
        object?[] args = [raw, propertyType, null];
        var ok = (bool)TryConvert.Invoke(null, args)!;
        return (ok, args[2]);
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly Guid SomeGuid = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff");

    public static TheoryData<object?, Type, bool, object?> Conversions() => new()
    {
        // Off the wire.
        { Json("null"), typeof(string), true, null },
        { Json("null"), typeof(int?), true, null },
        { Json("null"), typeof(int), false, null },
        { Json("5"), typeof(string), true, "5" },
        { Json("\"abc\""), typeof(string), true, "abc" },
        { Json($"\"{SomeGuid}\""), typeof(Guid), true, SomeGuid },
        { Json("\"not-a-guid\""), typeof(Guid), false, null },
        { Json("\"high\""), typeof(Level), true, Level.High },
        { Json("\"Urgent\""), typeof(Level), false, null },
        { Json("42"), typeof(int), true, 42 },
        { Json("42"), typeof(int?), true, 42 },
        { Json("\"42\""), typeof(int), false, null },
        { Json("true"), typeof(bool), true, true },
        { Json("12.5"), typeof(decimal), true, 12.5m },
        { Json("{}"), typeof(int), false, null },

        // In-process CLR values.
        { null, typeof(string), true, null },
        { null, typeof(int), false, null },
        { 5, typeof(int), true, 5 },
        { 5, typeof(long), true, 5L },
        { "high", typeof(Level), true, Level.High },
        { "abc", typeof(int), false, null },
    };

    [Theory]
    [MemberData(nameof(Conversions))]
    public void A_filter_value_converts_or_narrows_to_nothing(object? raw, Type propertyType, bool expectedOk, object? expected)
    {
        var (ok, value) = Convert(raw, propertyType);

        ok.Should().Be(expectedOk);
        value.Should().Be(expected);
    }

    // --- RowSortComparer ------------------------------------------------------

    public static TheoryData<object?, object?, int> Comparisons() => new()
    {
        { null, null, 0 },
        { null, "a", 1 },
        { "a", null, -1 },
        { "apple", "BANANA", -1 },
        { "Same", "same", 0 },
        { 2, 10, -1 },
        { 10, 2, 1 },
        { 1, "1", 0 },
        { new object(), new object(), 0 },
    };

    [Theory]
    [MemberData(nameof(Comparisons))]
    public void Rows_order_nulls_last_strings_case_insensitively_and_mixed_types_as_equal(object? x, object? y, int expectedSign)
    {
        Math.Sign(RowSortComparer.Instance.Compare(x, y)).Should().Be(expectedSign);
    }
}
