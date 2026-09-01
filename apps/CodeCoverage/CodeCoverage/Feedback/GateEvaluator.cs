using System.Globalization;
using CodeCoverage.Entities;
using CodeCoverage.Ingestion;
using CodeCoverage.Services;

namespace CodeCoverage.Feedback;

/// <param name="Conclusion">success | failure | neutral — GitHub's vocabulary, already resolved against Blocking.</param>
/// <param name="Passed">The raw verdict before Blocking softened it; null when the check abstained.</param>
public sealed record CheckVerdict(string Conclusion, bool? Passed, string Title, string Summary);

/// <summary>
/// Turns a build's numbers and its effective gate into the two published
/// check-run verdicts. Pure — every input arrives as an argument, so every
/// rule here is unit-testable without GitHub or RavenDB. The house rules:
/// a missing baseline or diff makes a check <b>neutral, never red</b> (#11:
/// abstaining is routine), and <c>Blocking: false</c> posts the same numbers
/// with a neutral conclusion (Codecov's `informational`).
/// </summary>
public static class GateEvaluator
{
    public static CheckVerdict Project(GateSettings gate, Build build, BuildComparer.Result comparison)
    {
        var basis = build.Partial ? gate.ProjectBasis : "whole";
        var headRate = basis == "projection" ? Rate(comparison.Partial?.Projection) : Rate(build.Coverage);
        if (headRate is null)
            return new CheckVerdict("neutral", null, "No coverage data", "The build measured no coverable lines, so there is nothing to judge.");

        var (baseRate, benchmark) = gate.ProjectMode == "fixed"
            ? (gate.ProjectTarget, "target")
            : (build.Partial && basis == "scoped" ? Rate(comparison.Partial?.ScopedBaseline) : Rate(comparison.Base.Coverage), "base");

        var summary = Describe(gate, build, comparison, basis, headRate.Value, baseRate);

        if (baseRate is null)
        {
            // Routine, not exceptional: cancelled base runs, first uploads,
            // unresolvable stacked bases. A gate that reddens here gets turned off.
            return new CheckVerdict("neutral", null, Inv($"{headRate:0.0}% — no baseline to compare against"), summary);
        }

        var passed = headRate.Value >= baseRate.Value - gate.ProjectThreshold;
        var delta = headRate.Value - baseRate.Value;
        var title = Inv($"{headRate:0.0}% ({delta:+0.0;-0.0;±0.0}% vs {benchmark} {baseRate:0.0}%)");
        return new CheckVerdict(Conclude(gate, passed), passed, title, summary);
    }

    public static CheckVerdict Patch(GateSettings gate, Build build)
    {
        var patch = build.Patch;
        if (patch is null)
            return new CheckVerdict("neutral", null, "No diff available", "Patch coverage needs a diff base (`base-sha` input or a pull request the App can see) — none was available for this build.");
        if (patch.LinesCoverable == 0)
            return new CheckVerdict("neutral", null, "No coverable added lines", $"The diff touches {patch.FilesInDiff} file(s), none of which added executable lines the reports measure.");

        var rate = patch.LinesCovered * 100.0 / patch.LinesCoverable;
        var truncation = patch.DiffTruncated ? " The diff hit GitHub's 300-file cap, so this under-reports." : "";
        var detail = Inv($"{patch.LinesCovered} of {patch.LinesCoverable} added lines covered across {patch.FilesMatched} measured file(s) ") +
                     Inv($"({patch.FilesInDiff} in the diff; unmeasured files are skipped, not counted as misses).{truncation}");

        if (gate.PatchTarget is null)
            return new CheckVerdict("neutral", null, Inv($"{rate:0.0}% of added lines covered"), $"{detail}\n\nNo patch target is configured — informational only.");

        var passed = rate >= gate.PatchTarget.Value - gate.PatchThreshold;
        var title = Inv($"{rate:0.0}% of added lines covered (target {gate.PatchTarget:0.0}%)");
        return new CheckVerdict(Conclude(gate, passed), passed, title, detail);
    }

    private static string Conclude(GateSettings gate, bool passed)
        => !gate.Blocking ? "neutral" : passed ? "success" : "failure";

    private static string Describe(GateSettings gate, Build build, BuildComparer.Result comparison, string basis, double headRate, double? baseRate)
    {
        var lines = new List<string>();

        if (build.Partial)
        {
            lines.Add($"Partial upload (nx affected) judged on the **{basis}** basis.");
            var resolved = comparison.Base;
            if (resolved.ResolvedSha is not null)
            {
                lines.Add(resolved.Mode == ResolvedBase.Exact
                    ? $"Base: `{resolved.ResolvedSha}` (as declared)."
                    : $"Base: `{resolved.ResolvedSha}` via **{resolved.Mode}** — the declared base `{resolved.RequestedSha ?? "(none)"}` had no usable coverage.");
            }
            if (basis == "projection" && comparison.IncompleteReasons.Length > 0)
                lines.Add($"⚠ The projection is best-effort ({string.Join(", ", comparison.IncompleteReasons)}); treat the number as incomplete.");
        }

        lines.Add(baseRate is null
            ? Inv($"Head: {headRate:0.0}%. No baseline resolved — informational.")
            : Inv($"Head: {headRate:0.0}%, benchmark: {baseRate:0.0}%, allowed drop: {gate.ProjectThreshold:0.0} points."));

        if (!gate.Blocking)
            lines.Add("This check is informational (Blocking is off in the repository's coverage gate).");

        return string.Join("\n\n", lines);
    }

    /// <summary>Check-run text goes to GitHub, not to this server's locale.</summary>
    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static double? Rate(CoverageSummary? summary)
        => summary is { LinesCoverable: > 0 } ? summary.LinesCovered * 100.0 / summary.LinesCoverable : null;
}
