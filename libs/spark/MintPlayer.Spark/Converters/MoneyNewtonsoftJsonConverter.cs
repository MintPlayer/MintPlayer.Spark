using MintPlayer.Spark.Abstractions;
using Newtonsoft.Json;

namespace MintPlayer.Spark.Converters;

/// <summary>
/// Newtonsoft.Json converter for <see cref="Money"/>, used by RavenDB for document storage. Writes and
/// reads the one shape <see cref="MoneyJsonConverter"/> uses on the API, <c>{"Amount":12.30,"Currency":"EUR"}</c>
/// (commerce X4), with the CLR member names so <c>x.Price.Amount</c> in a query or an index map addresses
/// the stored field.
/// </summary>
/// <remarks>
/// The amount is read with <see cref="JsonReader.ReadAsDecimal"/>, never through a <c>double</c>: RavenDB's
/// reader hands numbers over lazily, and going through a binary float would be the one place money could
/// pick up a rounding error.
/// </remarks>
internal sealed class MoneyNewtonsoftJsonConverter : Newtonsoft.Json.JsonConverter<Money>
{
    public override Money? ReadJson(JsonReader reader, Type objectType, Money? existingValue, bool hasExistingValue, Newtonsoft.Json.JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
            return null;
        if (reader.TokenType != JsonToken.StartObject)
            throw new JsonSerializationException("Money must be an object: {\"Amount\":12.30,\"Currency\":\"EUR\"}.");

        decimal? amount = null;
        string? currency = null;
        while (reader.Read() && reader.TokenType != JsonToken.EndObject)
        {
            var name = reader.Value as string;
            if (string.Equals(name, MoneyJsonConverter.AmountName, StringComparison.OrdinalIgnoreCase))
            {
                amount = reader.ReadAsDecimal();
            }
            else if (string.Equals(name, MoneyJsonConverter.CurrencyName, StringComparison.OrdinalIgnoreCase))
            {
                currency = reader.ReadAsString();
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        if (amount is null || string.IsNullOrWhiteSpace(currency))
            throw new JsonSerializationException("Money needs both an Amount and a Currency.");

        try { return new Money(amount.Value, currency); }
        catch (ArgumentException ex) { throw new JsonSerializationException(ex.Message, ex); }
    }

    public override void WriteJson(JsonWriter writer, Money? value, Newtonsoft.Json.JsonSerializer serializer)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteStartObject();
        writer.WritePropertyName(MoneyJsonConverter.AmountName);
        writer.WriteValue(value.Amount);
        writer.WritePropertyName(MoneyJsonConverter.CurrencyName);
        writer.WriteValue(value.Currency);
        writer.WriteEndObject();
    }
}
