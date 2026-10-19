using System.Text.Json;
using System.Text.Json.Serialization;
using MintPlayer.Spark.Abstractions;

// The fixtures' claims: each namespace is generated on its own (SparkContractGeneratorTests picks the roots
// by namespace). "Unclaimed" is deliberately left out.
[assembly: SparkClientModule("MintPlayer.Spark.Tests.Contracts.Fixtures.Wire", SparkClientModuleAttribute.Application)]
[assembly: SparkClientModule("MintPlayer.Spark.Tests.Contracts.Fixtures.Converted", SparkClientModuleAttribute.Application)]

namespace MintPlayer.Spark.Tests.Contracts.Fixtures.Wire
{
    /// <summary>Every wire rule the generator states, on one record.</summary>
    [SparkContract]
    public sealed record WireShape(
        string Name,
        string? Nickname,
        long Big,
        decimal Price,
        int? Count,
        Guid Id,
        DateTimeOffset At,
        [property: JsonPropertyName("custom_name")] string Renamed,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Sometimes,
        NumberedKind Kind,
        NamedKind Named,
        FlaggedKind Flags,
        [property: JsonConverter(typeof(JsonStringEnumConverter<NumberedKind>))] NumberedKind KindByName,
        IReadOnlyList<string?> Tags,
        Dictionary<string, int> Scores,
        WireChild Child,
        WireChild? OptionalChild,
        TranslatedString Label,
        PersistentObject Object,
        object? Anything)
    {
        /// <summary>Never on the wire.</summary>
        [JsonIgnore]
        public string Hidden { get; init; } = "";
    }

    public sealed record WireChild(string Value);

    /// <summary>No converter: written as its number.</summary>
    public enum NumberedKind
    {
        First = 1,
        Second = 2,
    }

    /// <summary>A string converter on the type: written as its names.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<NamedKind>))]
    public enum NamedKind
    {
        [JsonStringEnumMemberName("alpha")] Alpha,
        Beta,
    }

    /// <summary>A [Flags] enum written by name: "A, B", which no union states.</summary>
    [Flags]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum FlaggedKind
    {
        None = 0,
        A = 1,
        B = 2,
    }

    [SparkContract("RenamedForTheClient")]
    public sealed record Renamed(string Value);

    [SparkContract]
    public class BaseShape
    {
        public string BaseValue { get; set; } = "";
    }

    [SparkContract]
    public sealed class DerivedShape : BaseShape
    {
        public int DerivedValue { get; set; }
    }
}

namespace MintPlayer.Spark.Tests.Contracts.Fixtures.Unclaimed
{
    /// <summary>A contract in a namespace no module claims.</summary>
    [SparkContract]
    public sealed record Orphan(string Value);
}

namespace MintPlayer.Spark.Tests.Contracts.Fixtures.Converted
{
    /// <summary>Reaches a type whose converter writes something other than its properties.</summary>
    [SparkContract]
    public sealed record HoldsConverted(Opaque Value);

    [JsonConverter(typeof(OpaqueConverter))]
    public sealed class Opaque
    {
        public string Inner { get; set; } = "";
    }

    public sealed class OpaqueConverter : JsonConverter<Opaque>
    {
        public override Opaque Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => new() { Inner = reader.GetString() ?? "" };

        public override void Write(Utf8JsonWriter writer, Opaque value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.Inner);
    }
}
