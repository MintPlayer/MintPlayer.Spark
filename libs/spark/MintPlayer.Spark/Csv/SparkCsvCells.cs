using System.Globalization;
using System.Text.Json;
using MintPlayer.Spark.Abstractions.Csv;

namespace MintPlayer.Spark.Csv;

/// <summary>
/// Reads one CSV cell as the JSON value an attribute of a data type takes on the wire (the value the web client would
/// post), so an imported row goes through exactly the save path a typed-in row does. The inverse of
/// <see cref="SparkCsv.FormatValue"/>: what an export writes reads back as the same value.
/// </summary>
/// <remarks>
/// Numbers are invariant (a dot, no grouping) whatever the delimiter; a comma decimal is refused rather than guessed,
/// because <c>1,234</c> is a thousand in one locale and one in another. A cell an export guarded (<c>'=…</c>) is
/// unguarded first. An array cell is JSON (<c>[…]</c>) or values separated by <c>|</c>. A complex value (AsDetail,
/// TranslatedString, Money, Quantity) is JSON when the cell starts with <c>{</c> or <c>[</c>, else plain text, which
/// the attribute's own converter then reads (a TranslatedString key, say) or the save refuses.
/// </remarks>
internal static class SparkCsvCells
{
    /// <summary>The JSON of <paramref name="cell"/> for an attribute of <paramref name="dataType"/>, or why it cannot be.</summary>
    public static bool TryConvert(string cell, string dataType, bool isArray, out JsonElement value, out string? error)
    {
        var text = SparkCsv.Unguard(cell);
        if (isArray && !IsComplex(dataType))
        {
            if (text.TrimStart().StartsWith('['))
                return TryParseJson(text, out value, out error);

            var items = new List<JsonElement>();
            foreach (var part in text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!TryScalar(SparkCsv.Unguard(part), dataType, out var item, out error))
                {
                    value = default;
                    return false;
                }
                items.Add(item);
            }
            value = JsonSerializer.SerializeToElement(items);
            error = null;
            return true;
        }
        return TryScalar(text, dataType, out value, out error);
    }

    private static bool TryScalar(string text, string dataType, out JsonElement value, out string? error)
    {
        error = null;
        var trimmed = text.Trim();
        switch (dataType.ToLowerInvariant())
        {
            case "number":
                if (long.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
                    return Ok(whole, out value);
                return Fail($"'{trimmed}' is not a whole number.", out value, out error);

            case "decimal":
                if (decimal.TryParse(trimmed, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                        CultureInfo.InvariantCulture, out var number))
                    return Ok(number, out value);
                return Fail($"'{trimmed}' is not a number (use a dot for decimals, no thousands separator).", out value, out error);

            case "boolean":
                switch (trimmed.ToLowerInvariant())
                {
                    case "true" or "1" or "yes" or "y":
                        return Ok(true, out value);
                    case "false" or "0" or "no" or "n":
                        return Ok(false, out value);
                    default:
                        return Fail($"'{trimmed}' is not true or false.", out value, out error);
                }

            case "date":
                if (DateOnly.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    return Ok(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), out value);
                return Fail($"'{trimmed}' is not a date (yyyy-MM-dd).", out value, out error);

            case "datetime":
                // ISO 8601. Without an offset the time is UTC, as an export writes it — never the server's zone (K10).
                if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
                    && trimmed.Length >= 10 && trimmed[4] == '-')
                    return Ok(SparkCsv.FormatValue(at), out value);
                return Fail($"'{trimmed}' is not a date and time (ISO 8601, e.g. 2026-10-19T08:30:00Z).", out value, out error);

            case "guid":
                if (Guid.TryParse(trimmed, out var guid))
                    return Ok(guid.ToString("D"), out value);
                return Fail($"'{trimmed}' is not a GUID.", out value, out error);

            default:
                if (IsComplex(dataType) && trimmed.Length > 0 && trimmed[0] is '{' or '[')
                    return TryParseJson(trimmed, out value, out error);
                return Ok(text, out value);
        }
    }

    private static bool IsComplex(string dataType)
        => dataType.ToLowerInvariant() is "asdetail" or "translatedstring" or "money" or "quantity";

    private static bool TryParseJson(string text, out JsonElement value, out string? error)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            value = document.RootElement.Clone();
            error = null;
            return true;
        }
        catch (JsonException)
        {
            return Fail("The value is not valid JSON.", out value, out error);
        }
    }

    private static bool Ok<T>(T raw, out JsonElement value)
    {
        value = JsonSerializer.SerializeToElement(raw);
        return true;
    }

    private static bool Fail(string message, out JsonElement value, out string? error)
    {
        value = default;
        error = message;
        return false;
    }
}
