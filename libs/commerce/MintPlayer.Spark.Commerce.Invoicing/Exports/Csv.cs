using System.Globalization;
using System.Text;
using MintPlayer.Spark.Abstractions.Csv;

namespace MintPlayer.Spark.Commerce.Invoicing.Exports;

/// <summary>
/// RFC 4180 CSV with a documented column set (GB4: "so accountants can map it"): comma-separated, CRLF, UTF-8 with a
/// BOM (spreadsheet programs then read the accents), invariant numbers with a dot. Text cells that a spreadsheet would
/// run as a formula (<c>= + - @</c>, tab, CR) are prefixed with an apostrophe (CSV injection); numbers never are.
/// </summary>
internal sealed class Csv
{
    private readonly StringBuilder sb = new();

    public Csv(params string[] header) => Row(header.Select(h => (object?)h).ToArray());

    public Csv Row(params object?[] cells)
    {
        for (var i = 0; i < cells.Length; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append(Cell(cells[i]));
        }
        sb.Append("\r\n");
        return this;
    }

    public byte[] ToBytes() => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];

    public override string ToString() => sb.ToString();

    private static string Cell(object? value)
    {
        var text = value switch
        {
            null => "",
            decimal d => d.ToString("0.00##", CultureInfo.InvariantCulture),
            int or long => Convert.ToString(value, CultureInfo.InvariantCulture)!,
            DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTimeOffset at => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            DateTime at => DateTime.SpecifyKind(at, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            _ => SparkCsv.Guard(value.ToString() ?? ""),
        };
        // Quoting is the shared codec's (warehouse WX11); the guard above applies to text only, so numbers stay numbers.
        return SparkCsv.Field(text, ',', guard: false);
    }
}
