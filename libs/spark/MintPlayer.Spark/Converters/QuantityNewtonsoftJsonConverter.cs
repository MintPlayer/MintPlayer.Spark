using MintPlayer.Spark.Abstractions;
using Newtonsoft.Json;

namespace MintPlayer.Spark.Converters;

/// <summary>
/// Newtonsoft.Json converter for <see cref="Quantity"/>, used by RavenDB for document storage. Writes and
/// reads the one shape <see cref="QuantityJsonConverter"/> uses on the API, <c>{"Amount":12,"Unit":"EA"}</c>
/// (warehouse WX8), with the CLR member names so <c>x.OnHand.Amount</c> in a query or an index map addresses
/// the stored field.
/// </summary>
/// <remarks>
/// The amount is read with <see cref="JsonReader.ReadAsDecimal"/>, never through a <c>double</c>: RavenDB's
/// reader hands numbers over lazily, and going through a binary float would be the one place a quantity could
/// pick up a rounding error.
/// </remarks>
internal sealed class QuantityNewtonsoftJsonConverter : Newtonsoft.Json.JsonConverter<Quantity>
{
    public override Quantity? ReadJson(JsonReader reader, Type objectType, Quantity? existingValue, bool hasExistingValue, Newtonsoft.Json.JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
            return null;
        if (reader.TokenType != JsonToken.StartObject)
            throw new JsonSerializationException("Quantity must be an object: {\"Amount\":12,\"Unit\":\"EA\"}.");

        decimal? amount = null;
        string? unit = null;
        while (reader.Read() && reader.TokenType != JsonToken.EndObject)
        {
            var name = reader.Value as string;
            if (string.Equals(name, QuantityJsonConverter.AmountName, StringComparison.OrdinalIgnoreCase))
            {
                amount = reader.ReadAsDecimal();
            }
            else if (string.Equals(name, QuantityJsonConverter.UnitName, StringComparison.OrdinalIgnoreCase))
            {
                unit = reader.ReadAsString();
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        if (amount is null || string.IsNullOrWhiteSpace(unit))
            throw new JsonSerializationException("Quantity needs both an Amount and a Unit.");

        try { return new Quantity(amount.Value, unit); }
        catch (ArgumentException ex) { throw new JsonSerializationException(ex.Message, ex); }
    }

    public override void WriteJson(JsonWriter writer, Quantity? value, Newtonsoft.Json.JsonSerializer serializer)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteStartObject();
        writer.WritePropertyName(QuantityJsonConverter.AmountName);
        writer.WriteValue(value.Amount);
        writer.WritePropertyName(QuantityJsonConverter.UnitName);
        writer.WriteValue(value.Unit);
        writer.WriteEndObject();
    }
}
