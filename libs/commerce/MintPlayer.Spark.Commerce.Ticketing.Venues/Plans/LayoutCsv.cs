using System.Globalization;
using System.Text;
using MintPlayer.Spark.Abstractions.Csv;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Models;

namespace MintPlayer.Spark.Commerce.Ticketing.Venues.Plans;

/// <summary>
/// CSV seat import and export (D32). One line per numbered seat, RFC 4180 quoting, invariant numbers:
/// <code>block,row,seat,x,y,category,tags,companion_of,key</code>
/// <c>block</c> is the block key, <c>row</c> and <c>seat</c> the printed labels, <c>tags</c> a <c>|</c>-separated
/// list of <c>accessible</c>, <c>companion</c>, <c>restricted-view</c>. <c>key</c> is optional on import (made from
/// block, row and seat). The browser editor has the same format (<c>layout-csv.ts</c>).
/// </summary>
/// <remarks>
/// Import replaces the rows of every block the file names and leaves the others alone; a block the plan lacks is
/// created as a numbered block outlined by its seats' bounding box. Export guards spreadsheet formula injection: a
/// text cell starting with <c>= + - @</c>, tab or carriage return is prefixed with an apostrophe (OWASP CSV injection).
/// </remarks>
public static class LayoutCsv
{
    public static readonly string[] Header = ["block", "row", "seat", "x", "y", "category", "tags", "companion_of", "key"];

    public static string Export(LayoutPlan plan)
    {
        var builder = new StringBuilder();
        builder.Append(string.Join(',', Header)).Append("\r\n");
        foreach (var block in plan.Blocks.Where(b => b.Kind == BlockKind.SeatedNumbered))
            foreach (var row in block.Rows)
                foreach (var seat in row.Seats)
                {
                    builder.Append(Text(block.Key)).Append(',')
                        .Append(Text(row.Label)).Append(',')
                        .Append(Text(seat.Label)).Append(',')
                        .Append(seat.X.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                        .Append(seat.Y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                        .Append(Text(seat.Category ?? "")).Append(',')
                        .Append(Text(TagsText(seat.Tags))).Append(',')
                        .Append(Text(seat.CompanionOf ?? "")).Append(',')
                        .Append(Text(seat.Key)).Append("\r\n");
                }
        return builder.ToString();
    }

    public static string TagsText(int tags)
        => string.Join('|', SeatTags.Names.Where(n => (tags & n.Flag) != 0).Select(n => n.Name));

    /// <summary>The plan with the file's seats merged in, or the problems (line numbers are 1-based, the header is line 1).</summary>
    public static (LayoutPlan? Plan, IReadOnlyList<LayoutProblem> Problems) Import(LayoutPlan plan, string csv)
    {
        var problems = new List<LayoutProblem>();
        var records = Parse(csv);
        if (records.Count == 0 || !records[0].Select(h => h.Trim().ToLowerInvariant()).Take(5).SequenceEqual(Header.Take(5)))
            return (null, [new("line 1", "ticketingVenues.csv.header")]);

        var columns = records[0].Select((h, i) => (Name: h.Trim().ToLowerInvariant(), i)).ToDictionary(c => c.Name, c => c.i);
        string Cell(List<string> record, string name) => columns.TryGetValue(name, out var i) && i < record.Count ? Unguard(record[i].Trim()) : "";

        // Group in file order: block → row label → seats.
        var byBlock = new Dictionary<string, List<(string Row, LayoutSeat Seat)>>(StringComparer.Ordinal);
        var blockOrder = new List<string>();
        for (var line = 1; line < records.Count; line++)
        {
            var record = records[line];
            if (record.Count == 1 && record[0].Length == 0)
                continue;
            var path = $"line {line + 1}";
            var blockKey = Cell(record, "block");
            var rowLabel = Cell(record, "row");
            var seatLabel = Cell(record, "seat");
            if (!LayoutKeys.IsValid(blockKey) || rowLabel.Length == 0 || seatLabel.Length == 0)
            {
                problems.Add(new(path, "ticketingVenues.csv.missing"));
                continue;
            }
            if (!double.TryParse(Cell(record, "x"), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                || !double.TryParse(Cell(record, "y"), NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            {
                problems.Add(new(path, "ticketingVenues.csv.position"));
                continue;
            }
            var tags = 0;
            foreach (var name in Cell(record, "tags").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var flag = SeatTags.Names.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)).Flag;
                if (flag == 0)
                    problems.Add(new(path, "ticketingVenues.csv.tag"));
                tags |= flag;
            }
            var key = Cell(record, "key");
            if (key.Length == 0)
                key = Slug($"{blockKey}-{rowLabel}-{seatLabel}");
            var seat = new LayoutSeat
            {
                Key = key,
                Label = seatLabel,
                X = x,
                Y = y,
                Tags = tags,
                Category = Cell(record, "category") is { Length: > 0 } category ? category : null,
                CompanionOf = Cell(record, "companion_of") is { Length: > 0 } of ? of : null,
            };
            if (!byBlock.TryGetValue(blockKey, out var list))
            {
                byBlock[blockKey] = list = [];
                blockOrder.Add(blockKey);
            }
            list.Add((rowLabel, seat));
        }
        if (problems.Count > 0)
            return (null, problems);

        var result = Clone(plan);
        foreach (var blockKey in blockOrder)
        {
            var seats = byBlock[blockKey];
            var block = result.Blocks.FirstOrDefault(b => b.Key == blockKey);
            if (block is null)
            {
                block = new LayoutBlock { Key = blockKey, Name = blockKey, Kind = BlockKind.SeatedNumbered, Polygon = BoundingBox(seats.Select(s => s.Seat), margin: 10) };
                result.Blocks.Add(block);
            }
            else if (block.Kind != BlockKind.SeatedNumbered)
            {
                problems.Add(new($"block:{blockKey}", "ticketingVenues.csv.notNumbered"));
                continue;
            }
            var previous = block.Rows.ToDictionary(r => r.Label, r => r.Key, StringComparer.Ordinal);
            block.Rows = seats.GroupBy(s => s.Row, StringComparer.Ordinal)
                .Select(g => new LayoutRow
                {
                    Key = previous.TryGetValue(g.Key, out var rowKey) ? rowKey : Slug(g.Key),
                    Label = g.Key,
                    Seats = g.Select(s => s.Seat).ToList(),
                })
                .ToList();
        }
        return problems.Count > 0 ? (null, problems) : (result, []);
    }

    /// <summary>
    /// RFC 4180 records through the shared codec (<see cref="SparkCsv"/>, warehouse WX11): quoted fields may hold the
    /// delimiter, quotes (doubled) and line breaks. The delimiter is detected, so a file Excel saved with <c>;</c>
    /// (comma-decimal locales) imports too.
    /// </summary>
    public static List<List<string>> Parse(string csv) => SparkCsv.Parse(csv);

    private static string Text(string value) => SparkCsv.Field(value);

    /// <summary>Undoes <see cref="Text"/>'s guard on the way back in.</summary>
    private static string Unguard(string value) => SparkCsv.Unguard(value);

    /// <summary>A key from free text: letters, digits, <c>_ . -</c>; everything else becomes <c>-</c>; at most 64 characters.</summary>
    public static string Slug(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' ? c : '-');
        var slug = builder.ToString().Trim('-');
        if (slug.Length == 0) slug = "x";
        return slug.Length > 64 ? slug[..64] : slug;
    }

    private static List<double> BoundingBox(IEnumerable<LayoutSeat> seats, double margin)
    {
        var list = seats.ToList();
        var minX = list.Min(s => s.X) - margin;
        var minY = list.Min(s => s.Y) - margin;
        var maxX = list.Max(s => s.X) + margin;
        var maxY = list.Max(s => s.Y) + margin;
        return [minX, minY, maxX, minY, maxX, maxY, minX, maxY];
    }

    private static LayoutPlan Clone(LayoutPlan plan)
        => System.Text.Json.JsonSerializer.Deserialize<LayoutPlan>(System.Text.Json.JsonSerializer.Serialize(plan))!;
}
