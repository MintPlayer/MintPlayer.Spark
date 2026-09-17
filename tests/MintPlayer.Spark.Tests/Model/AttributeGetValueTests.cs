using System.Text.Json;
using MintPlayer.Spark.Abstractions;
using POA = MintPlayer.Spark.Abstractions.PersistentObjectAttribute;

namespace MintPlayer.Spark.Tests.Model;

/// <summary>
/// <see cref="POA.GetValue{T}"/> against the shape a value actually has inside a hook.
/// </summary>
/// <remarks>
/// The gap these close: a value that arrived from a client is a <see cref="JsonElement"/>, and
/// <c>Convert.ChangeType</c> throws <c>InvalidCastException</c> on one. So the obvious way to read
/// an attribute threw for every hook running against a submitted object, while the identical call
/// passed in a unit test that assigned a plain string. It surfaced only by driving the real app —
/// the shipped samples all use <c>Value?.ToString()</c> and never met it.
/// </remarks>
public class AttributeGetValueTests
{
    private static POA Wire(string json) =>
        new() { Name = "A", Value = JsonDocument.Parse(json).RootElement.Clone() };

    [Fact]
    public void A_wire_string_reads_as_a_string()
    {
        Wire("\"fixed\"").GetValue<string>().Should().Be("fixed");
    }

    [Fact]
    public void A_wire_number_reads_as_a_number()
    {
        Wire("80").GetValue<int>().Should().Be(80);
        Wire("80.5").GetValue<double>().Should().Be(80.5);
    }

    [Fact]
    public void A_wire_boolean_reads_as_a_boolean()
    {
        Wire("true").GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void A_wire_null_reads_as_the_default()
    {
        Wire("null").GetValue<string>().Should().BeNull();
        Wire("null").GetValue<int>().Should().Be(0);
    }

    [Fact]
    public void A_nullable_target_is_satisfied_by_a_wire_number()
    {
        // Nullable<T> is not what ChangeType wants; the underlying type is.
        Wire("80").GetValue<double?>().Should().Be(80);
    }

    [Fact]
    public void A_plain_CLR_value_still_reads_as_before()
    {
        // The pre-existing path, which a hook meets when the object was scaffolded server-side
        // rather than submitted.
        new POA { Name = "A", Value = "fixed" }.GetValue<string>().Should().Be("fixed");
        new POA { Name = "A", Value = 80 }.GetValue<int>().Should().Be(80);
    }

    [Fact]
    public void An_absent_value_reads_as_the_default()
    {
        new POA { Name = "A" }.GetValue<string>().Should().BeNull();
    }
}
