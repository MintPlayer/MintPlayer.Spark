using CodeCoverage.Entities;
using CodeCoverage.Git;
using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.PrSize;

/// <summary>
/// Keeps a scale and an ignore list that the forge would refuse, or that would label nothing, from being stored
/// (docs/coverage_pr_size_labels_PRD.md D2, D5). Every message names its row's cell, so the inline list shows it there.
/// </summary>
public static class PrSizeValidation
{
    /// <summary>GitHub's own label form caps a name at 50 characters (S1); the REST limit is not lower.</summary>
    public const int MaxNameLength = 50;

    /// <summary>Measured in S1: 101 characters gives "Description is too long (maximum is 100 characters)".</summary>
    public const int MaxDescriptionLength = 100;

    /// <summary>The account's scale and ignore rules, and the switch: enabling with an empty scale is refused.</summary>
    public static void ValidateAccount(Account account)
    {
        ValidateScale(account.PrSizeLabelScale, nameof(Account.PrSizeLabelScale));
        ValidateIgnoreRules(account.PrSizeIgnoreRules, nameof(Account.PrSizeIgnoreRules));

        if (account.PrSizeLabels && account.PrSizeLabelScale.Count == 0)
            throw new SparkValidationException(
                "PR size labels need at least one row in the size scale.", nameof(Account.PrSizeLabelScale));
    }

    /// <summary>A repository's extra ignore rules.</summary>
    public static void ValidateRepository(Repository repository)
        => ValidateIgnoreRules(repository.PrSizeIgnoreRules, nameof(Repository.PrSizeIgnoreRules));

    public static void ValidateScale(IReadOnlyList<PrSizeLabel> scale, string attribute)
    {
        var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var thresholds = new Dictionary<int, int>();
        for (var i = 0; i < scale.Count; i++)
        {
            var row = scale[i];
            var name = row.Name?.Trim() ?? string.Empty;
            if (name.Length == 0)
                throw new SparkValidationException($"Row {i + 1} needs a label name.", Cell(attribute, i, nameof(PrSizeLabel.Name)));
            if (name.Length > MaxNameLength)
                throw new SparkValidationException(
                    $"Label \"{name}\" is longer than {MaxNameLength} characters.", Cell(attribute, i, nameof(PrSizeLabel.Name)));
            // GitHub label names are case-insensitive (S1: "Name has already been taken").
            if (names.TryGetValue(name, out var other))
                throw new SparkValidationException(
                    $"Label \"{name}\" is already used by row {other + 1}; names are compared case-insensitively.",
                    Cell(attribute, i, nameof(PrSizeLabel.Name)));
            names[name] = i;

            if (row.MinLines < 0)
                throw new SparkValidationException(
                    $"\"{name}\" starts at a negative number of lines.", Cell(attribute, i, nameof(PrSizeLabel.MinLines)));
            if (thresholds.TryGetValue(row.MinLines, out var same))
                throw new SparkValidationException(
                    $"\"{name}\" starts at {row.MinLines} lines, as row {same + 1} does; every row needs its own threshold.",
                    Cell(attribute, i, nameof(PrSizeLabel.MinLines)));
            thresholds[row.MinLines] = i;

            if (row.Description is { Length: > MaxDescriptionLength })
                throw new SparkValidationException(
                    $"The description of \"{name}\" is longer than {MaxDescriptionLength} characters.",
                    Cell(attribute, i, nameof(PrSizeLabel.Description)));
        }

        // Every pull request must get a label, so the smallest row starts at 0.
        if (scale.Count > 0 && !thresholds.ContainsKey(0))
        {
            var smallest = scale.Select((row, i) => (row, i)).MinBy(r => r.row.MinLines);
            throw new SparkValidationException(
                $"The smallest row, \"{smallest.row.Name}\", must start at 0 lines, so every pull request gets a label.",
                Cell(attribute, smallest.i, nameof(PrSizeLabel.MinLines)));
        }
    }

    public static void ValidateIgnoreRules(IReadOnlyList<PrSizeIgnoreRule> rules, string attribute)
    {
        for (var i = 0; i < rules.Count; i++)
        {
            var glob = rules[i].Glob?.Trim() ?? string.Empty;
            var pattern = glob.StartsWith('!') ? glob[1..] : glob;
            if (string.IsNullOrWhiteSpace(pattern))
                throw new SparkValidationException($"Rule {i + 1} needs a path glob.", Cell(attribute, i, nameof(PrSizeIgnoreRule.Glob)));
            try
            {
                _ = new PathGlob(pattern);
            }
            catch (ArgumentException ex)
            {
                throw new SparkValidationException(
                    $"\"{glob}\" is not a valid path glob: {ex.Message}", Cell(attribute, i, nameof(PrSizeIgnoreRule.Glob)));
            }
        }
    }

    /// <summary>The form's inline error path, <c>attr[row].col</c> (spark-po-form.component.ts).</summary>
    private static string Cell(string attribute, int row, string column) => $"{attribute}[{row}].{column}";
}
