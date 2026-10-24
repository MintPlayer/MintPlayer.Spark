using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Git;
using CodeCoverage.LookupReferences;

namespace CodeCoverage.PrSize;

/// <summary>How one file counted towards the size.</summary>
public enum PrSizeFileOutcome
{
    /// <summary>Additions and deletions both count.</summary>
    Counted,
    /// <summary>Not counted at all.</summary>
    Ignored,
    /// <summary>Only <c>max(deletions − additions, 0)</c> counts.</summary>
    DeletionsOnly,
}

/// <summary>One row of the check run's breakdown (PRD D7): the file, how it counted, and the rule that decided it.</summary>
public sealed record PrSizeFileResult(string Path, int Additions, int Deletions, int Counted, PrSizeFileOutcome Outcome, string? Rule);

/// <summary>A pull request's size and how it was reached.</summary>
/// <param name="Size">The changed lines that count.</param>
/// <param name="Files">Per listed file, in listing order; empty when the totals were used.</param>
/// <param name="UnlistedLines">Lines of files the forge did not list (above its cap), counted at the PR's totals.</param>
public sealed record PrSizeResult(int Size, IReadOnlyList<PrSizeFileResult> Files, int UnlistedLines);

/// <summary>
/// The size of a pull request (docs/coverage_pr_size_labels_PRD.md D5): the sum of additions and deletions over the files
/// no rule ignores, where an ignore-additions file counts only its net deletions.
/// </summary>
/// <remarks>
/// The rules are the account's followed by the repository's. Per path the LAST matching rule decides, gitignore-style, and
/// a glob starting with <c>!</c> counts the file fully. Ignoring can only lower the size, so the PR's raw totals are an
/// upper bound, which is what the forge's file cap falls back to.
/// </remarks>
public sealed class PrSizeCalculator
{
    /// <summary>GitHub lists at most this many files of a pull request.</summary>
    public const int ForgeFileCap = 3000;

    private readonly (PathGlob Glob, bool Include, EPrSizeIgnoreMode Mode, string Text)[] rules;

    public PrSizeCalculator(IEnumerable<PrSizeIgnoreRule> rules)
        => this.rules = rules
            .Where(r => !string.IsNullOrWhiteSpace(r.Glob))
            .Select(r =>
            {
                var text = r.Glob.Trim();
                var include = text.StartsWith('!');
                var pattern = include ? text[1..] : text;
                return string.IsNullOrWhiteSpace(pattern) ? default : (new PathGlob(pattern), include, r.Mode, text);
            })
            .Where(r => r.Item1 is not null)
            .ToArray();

    /// <summary>The PR's size from its totals alone: used above the forge's file cap.</summary>
    public static PrSizeResult FromTotals(int additions, int deletions)
        => new(additions + deletions, [], additions + deletions);

    /// <summary>
    /// The size from the listed files. When the forge listed fewer files than the PR changes, the lines it did not list
    /// count fully, taken as the PR's totals minus what was listed.
    /// </summary>
    public PrSizeResult Compute(IReadOnlyList<ForgePullRequestFile> files, int totalAdditions, int totalDeletions)
    {
        var results = new List<PrSizeFileResult>(files.Count);
        var size = 0;
        var listedLines = 0;
        foreach (var file in files)
        {
            listedLines += file.Additions + file.Deletions;
            var (outcome, rule) = Classify(file.Path);
            var counted = outcome switch
            {
                PrSizeFileOutcome.Ignored => 0,
                PrSizeFileOutcome.DeletionsOnly => Math.Max(file.Deletions - file.Additions, 0),
                _ => file.Additions + file.Deletions,
            };
            size += counted;
            results.Add(new PrSizeFileResult(file.Path, file.Additions, file.Deletions, counted, outcome, rule));
        }

        var unlisted = Math.Max(totalAdditions + totalDeletions - listedLines, 0);
        return new PrSizeResult(size + unlisted, results, unlisted);
    }

    /// <summary>How the last matching rule says <paramref name="path"/> counts, and that rule's glob.</summary>
    public (PrSizeFileOutcome Outcome, string? Rule) Classify(string path)
    {
        for (var i = rules.Length - 1; i >= 0; i--)
        {
            var rule = rules[i];
            if (!rule.Glob.Matches(path))
                continue;
            if (rule.Include)
                return (PrSizeFileOutcome.Counted, rule.Text);
            return (rule.Mode == EPrSizeIgnoreMode.IgnoreAdditions ? PrSizeFileOutcome.DeletionsOnly : PrSizeFileOutcome.Ignored, rule.Text);
        }
        return (PrSizeFileOutcome.Counted, null);
    }
}
