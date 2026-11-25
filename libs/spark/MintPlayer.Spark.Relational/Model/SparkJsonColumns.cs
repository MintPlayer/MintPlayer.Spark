using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Relational.Model;

/// <summary>
/// A value without a relational shape as a JSON column (#536 D1): <c>TranslatedString</c>, dictionaries, embedded
/// objects. The comparer compares the JSON, so a change inside the value is a change of the column.
/// </summary>
internal static class SparkJsonColumns
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.General);
        options.Converters.Add(new IdJsonConverterFactory());
        return options;
    }

    public static ValueConverter Converter(Type type) => (ValueConverter)Activator.CreateInstance(typeof(JsonConverter<>).MakeGenericType(type))!;

    public static ValueComparer Comparer(Type type) => (ValueComparer)Activator.CreateInstance(typeof(JsonComparer<>).MakeGenericType(type))!;

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    private sealed class JsonConverter<T>() : ValueConverter<T, string>(v => Write(v), s => Read<T>(s)!);

    private sealed class JsonComparer<T>() : ValueComparer<T>(
        (a, b) => Write(a) == Write(b),
        v => v == null ? 0 : Write(v).GetHashCode(),
        v => Read<T>(Write(v))!);
}
