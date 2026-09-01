using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.Entities;

/// <summary>
/// The repository's gate policy — what the check-runs and any consumer-side
/// ratchet judge against. The stored document is authoritative; an optional
/// <c>coverage.yml</c> in the repo (read from the **base ref**, so a PR cannot
/// rewrite the policy it is judged by) overrides per field at verdict time and
/// is snapshotted onto the Build (roadmap §7.1).
/// </summary>
public class GateSettings
{
    /// <summary>"auto" ratchets against the resolved base; "fixed" compares to <see cref="ProjectTarget"/> and needs no base at all.</summary>
    public string ProjectMode { get; set; } = "auto";

    /// <summary>Percent target for fixed mode (e.g. 80 = 80%).</summary>
    public double? ProjectTarget { get; set; }

    /// <summary>Allowed drop in percentage points before the project status fails.</summary>
    public double ProjectThreshold { get; set; }

    /// <summary>
    /// Which number a partial build's project status judges: "scoped"
    /// (like-for-like, #11) or "projection" (patched whole-workspace).
    /// </summary>
    public string ProjectBasis { get; set; } = "scoped";

    /// <summary>Percent target for patch coverage; null disables the patch gate.</summary>
    public double? PatchTarget { get; set; }

    /// <summary>Allowed shortfall in percentage points below <see cref="PatchTarget"/>.</summary>
    public double PatchThreshold { get; set; }

    /// <summary>
    /// False (the default) posts informational check-runs that never fail —
    /// Codecov's `informational` on-ramp. Nothing blocks until a human opts in.
    /// </summary>
    public bool Blocking { get; set; }
}
