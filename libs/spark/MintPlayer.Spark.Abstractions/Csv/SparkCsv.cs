using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MintPlayer.Spark.Abstractions.Csv;

/// <summary>
/// The one CSV codec of Spark and its packages (warehouse WX11, shared with the commerce seat CSV D32, the invoicing
/// exports and the seller exports): RFC 4180 records, culture-invariant values and the CSV-injection guard.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Reading</b> (<see cref="Parse"/>): quoted fields may hold the delimiter, doubled quotes and line breaks; CRLF,
/// LF and a lone CR all end a record; a leading UTF-8 BOM is skipped. Fields are returned as written, untrimmed.</item>
/// <item><b>Delimiter</b>: <c>,</c> by default; <c>;</c> is what Excel writes and expects in locales whose decimal
/// separator is a comma (Belgium, the Netherlands, France, Germany). <see cref="DetectDelimiter"/> picks
/// <c>,</c>, <c>;</c> or tab from the header record.</item>
/// <item><b>Writing</b> (<see cref="Field"/>, <see cref="FormatValue"/>, <see cref="SparkCsvWriter"/>): numbers and dates
/// are invariant (a dot, ISO 8601, UTC), so a file reads back the same on every machine whatever the delimiter.</item>
/// <item><b>Injection</b> (OWASP "CSV injection"): a <em>text</em> cell starting with <c>= + - @</c>, tab or carriage
/// return is prefixed with an apostrophe, so a spreadsheet shows it instead of running it as a formula. Numbers are
/// never prefixed (a negative number stays a number). <see cref="Unguard"/> removes the prefix on the way back in.</item>
/// </list>
/// </remarks>
public static class SparkCsv
{
    /// <summary>The characters a spreadsheet treats as the start of a formula (OWASP), plus tab and CR.</summary>
    public const string FormulaStarts = "=+-@\t\r";

    /// <summary>The delimiters <see cref="DetectDelimiter"/> chooses between.</summary>
    public static readonly char[] KnownDelimiters = [',', ';', '\t'];

    /// <summary>
    /// The records of <paramref name="text"/>. A trailing line break does not add an empty record; an empty line in the
    /// middle is one record holding one empty field (callers usually skip it, see <see cref="IsBlank"/>).
    /// </summary>
    /// <param name="delimiter">The field separator; <see langword="null"/> detects it from the first record.</param>
    public static List<List<string>> Parse(string text, char? delimiter = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var separator = delimiter ?? DetectDelimiter(text);
        if (separator is '"' or '\r' or '\n')
            throw new ArgumentException("A quote or a line break cannot separate fields.", nameof(delimiter));

        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var fieldStarted = false;
        var start = text.Length > 0 && text[0] == '﻿' ? 1 : 0;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"')
            {
                quoted = true;
                fieldStarted = true;
            }
            else if (c == separator)
            {
                record.Add(field.ToString());
                field.Clear();
                fieldStarted = true;
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                record.Add(field.ToString());
                field.Clear();
                records.Add(record);
                record = [];
                fieldStarted = false;
            }
            else
            {
                field.Append(c);
                fieldStarted = true;
            }
        }
        if (fieldStarted || field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            records.Add(record);
        }
        return records;
    }

    /// <summary>Whether a parsed record is an empty line (one empty field, or none).</summary>
    public static bool IsBlank(IReadOnlyList<string> record) => record.Count == 0 || (record.Count == 1 && record[0].Length == 0);

    /// <summary>
    /// The delimiter of <paramref name="text"/>: whichever of <c>,</c>, <c>;</c> and tab occurs most often outside
    /// quotes in the first record, <c>,</c> on a tie or when none occurs.
    /// </summary>
    public static char DetectDelimiter(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int commas = 0, semicolons = 0, tabs = 0;
        var quoted = false;
        foreach (var c in text.AsSpan(text.Length > 0 && text[0] == '﻿' ? 1 : 0))
        {
            if (c == '"') quoted = !quoted;
            else if (quoted) continue;
            else if (c is '\r' or '\n') break;
            else if (c == ',') commas++;
            else if (c == ';') semicolons++;
            else if (c == '\t') tabs++;
        }
        if (semicolons > commas && semicolons >= tabs) return ';';
        if (tabs > commas && tabs > semicolons) return '\t';
        return ',';
    }

    /// <summary>
    /// One field as written: <paramref name="value"/> quoted when it holds the delimiter, a quote or a line break
    /// (quotes doubled), and guarded against formula injection first when <paramref name="guard"/> is set.
    /// </summary>
    public static string Field(string? value, char delimiter = ',', bool guard = true)
    {
        var text = value ?? "";
        if (guard)
            text = Guard(text);
        return NeedsQuotes(text, delimiter) ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
    }

    /// <summary>A cell of any value: <see cref="FormatValue"/>, then quoted; only text is guarded (see <see cref="SparkCsv"/>).</summary>
    public static string Cell(object? value, char delimiter = ',')
    {
        var (text, isText) = Format(value);
        return Field(text, delimiter, guard: isText);
    }

    /// <summary>Prefixes an apostrophe to text a spreadsheet would run as a formula.</summary>
    public static string Guard(string text) => text.Length > 0 && FormulaStarts.Contains(text[0]) ? "'" + text : text;

    /// <summary>Undoes <see cref="Guard"/>: an apostrophe followed by a formula start is dropped.</summary>
    public static string Unguard(string text) => text.Length > 1 && text[0] == '\'' && FormulaStarts.Contains(text[1]) ? text[1..] : text;

    /// <summary>
    /// The invariant text of a value: numbers with a dot and no grouping, <see cref="DateOnly"/> as <c>yyyy-MM-dd</c>,
    /// date-times in UTC ISO 8601, booleans <c>true</c>/<c>false</c>, a JSON string unwrapped, other JSON (arrays,
    /// objects) compact, anything else through <see cref="JsonSerializer"/> (so a <c>Money</c> or a
    /// <c>TranslatedString</c> reads back through its own converter). <see langword="null"/> is the empty string.
    /// </summary>
    public static string FormatValue(object? value) => Format(value).Text;

    private static (string Text, bool IsText) Format(object? value) => value switch
    {
        null => ("", false),
        string s => (s, true),
        char c => (c.ToString(), true),
        bool b => (b ? "true" : "false", false),
        decimal d => (d.ToString("0.############################", CultureInfo.InvariantCulture), false),
        double d => (d.ToString("R", CultureInfo.InvariantCulture), false),
        float f => (f.ToString("R", CultureInfo.InvariantCulture), false),
        sbyte or byte or short or ushort or int or uint or long or ulong => (Convert.ToString(value, CultureInfo.InvariantCulture)!, false),
        DateOnly date => (date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), false),
        TimeOnly time => (time.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture), false),
        DateTimeOffset at => (at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture), false),
        DateTime at => ((at.Kind == DateTimeKind.Local ? at.ToUniversalTime() : DateTime.SpecifyKind(at, DateTimeKind.Utc))
            .ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture), false),
        Guid g => (g.ToString("D"), false),
        Enum e => (e.ToString(), true),
        JsonElement element => FormatJson(element),
        _ => FormatJson(JsonSerializer.SerializeToElement(value, value.GetType())),
    };

    private static (string Text, bool IsText) FormatJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => ("", false),
        JsonValueKind.String => (element.GetString() ?? "", true),
        JsonValueKind.Number => (element.GetRawText(), false),
        JsonValueKind.True => ("true", false),
        JsonValueKind.False => ("false", false),
        // Compact JSON of an array or object: it starts with [ or {, never a formula start.
        _ => (JsonSerializer.Serialize(element), true),
    };

    private static bool NeedsQuotes(string text, char delimiter)
    {
        foreach (var c in text)
            if (c == delimiter || c is '"' or '\r' or '\n')
                return true;
        return false;
    }
}

/// <summary>Writes a CSV file: one header, then rows, CRLF line ends (RFC 4180).</summary>
/// <param name="delimiter">The field separator (<c>,</c>; <c>;</c> for Excel in comma-decimal locales).</param>
/// <param name="includeBom">Whether <see cref="ToBytes"/> starts with the UTF-8 BOM, which makes Excel read the file as UTF-8.</param>
public sealed class SparkCsvWriter(char delimiter = ',', bool includeBom = true)
{
    private readonly StringBuilder builder = new();

    public char Delimiter { get; } = delimiter is '"' or '\r' or '\n'
        ? throw new ArgumentException("A quote or a line break cannot separate fields.", nameof(delimiter))
        : delimiter;

    public bool IncludeBom { get; } = includeBom;

    /// <summary>The number of records written, the header included.</summary>
    public int Records { get; private set; }

    /// <summary>A header or any row of text cells (each guarded and quoted as needed).</summary>
    public SparkCsvWriter Header(params IEnumerable<string> names) => Row(names.Cast<object?>());

    /// <summary>One record; each cell through <see cref="SparkCsv.Cell"/>.</summary>
    public SparkCsvWriter Row(params IEnumerable<object?> cells)
    {
        var first = true;
        foreach (var cell in cells)
        {
            if (!first)
                builder.Append(Delimiter);
            builder.Append(SparkCsv.Cell(cell, Delimiter));
            first = false;
        }
        builder.Append("\r\n");
        Records++;
        return this;
    }

    /// <summary>The text, without a BOM.</summary>
    public override string ToString() => builder.ToString();

    /// <summary>The file as UTF-8, with the BOM when <see cref="IncludeBom"/>.</summary>
    public byte[] ToBytes()
    {
        var body = Encoding.UTF8.GetBytes(builder.ToString());
        return IncludeBom ? [.. Encoding.UTF8.GetPreamble(), .. body] : body;
    }
}
