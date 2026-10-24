using System.Drawing;
using CodeCoverage.Entities;

namespace CodeCoverage.PrSize;

/// <summary>
/// An account's scale, ordered by threshold (docs/coverage_pr_size_labels_PRD.md D2): which row a size falls in, and
/// what each row's label looks like on the forge.
/// </summary>
public sealed class PrSizeScale
{
    private readonly PrSizeLabel[] rows;

    public PrSizeScale(IEnumerable<PrSizeLabel> rows)
        => this.rows = rows.OrderBy(r => r.MinLines).ToArray();

    public IReadOnlyList<PrSizeLabel> Rows => rows;

    /// <summary>The row whose range holds <paramref name="size"/>, or null for an empty scale.</summary>
    /// <remarks>Validation keeps a 0 row in every stored scale; below the smallest row the smallest still answers.</remarks>
    public PrSizeLabel? LabelFor(int size)
    {
        if (rows.Length == 0)
            return null;
        var match = rows[0];
        foreach (var row in rows)
        {
            if (row.MinLines > size)
                break;
            match = row;
        }
        return match;
    }

    /// <summary>The smallest row's upper bound, exclusive: a raw total below it needs no file listing (S2).</summary>
    public int? LowestRowCeiling => rows.Length > 1 ? rows[1].MinLines : null;

    /// <summary>The label as the forge should carry it: name, six lower-case hex digits, and a description.</summary>
    public ManagedLabel Desired(PrSizeLabel row) => new()
    {
        RowId = row.Id,
        Name = row.Name.Trim(),
        Color = Hex(row.Color),
        Description = string.IsNullOrWhiteSpace(row.Description) ? GeneratedDescription(row) : row.Description.Trim(),
    };

    /// <summary>
    /// "Denotes a PR that changes 100-499 lines", from the row and the next one, so it stays right when the thresholds
    /// move (D2).
    /// </summary>
    public string GeneratedDescription(PrSizeLabel row)
    {
        var index = Array.IndexOf(rows, row);
        var next = index >= 0 && index + 1 < rows.Length ? rows[index + 1] : null;
        return next is null
            ? $"Denotes a PR that changes {row.MinLines}+ lines"
            : $"Denotes a PR that changes {row.MinLines}-{next.MinLines - 1} lines";
    }

    /// <summary>GitHub takes six hex digits without <c>#</c>; the alpha channel is dropped.</summary>
    public static string Hex(Color color) => $"{color.R:x2}{color.G:x2}{color.B:x2}";
}
