using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SeoTester.Catalogue;

namespace SeoTester.Scoring;

/// <summary>
/// What a run was scored with (PRD §3.1, §7, D32): the category weights and, per enabled check, its category,
/// severity and weight. A run stores it, so its scores can be recomputed and explained after an administrator edits
/// the catalogue or the weights. <see cref="Version"/> is a hash of it: two runs scored alike carry the same
/// version, and the trend chart marks the runs where it changes.
/// </summary>
/// <remarks>Plain settable members: the run document stores it as it is.</remarks>
public sealed class ScoringSnapshot
{
    /// <summary>
    /// The rules of <see cref="ScoreCalculator"/> this snapshot was made for. Part of <see cref="Version"/>, so a
    /// change to the rules (say, the critical cap) also marks the trend. Bump it with any such change.
    /// </summary>
    public const int CurrentAlgorithm = 1;

    public int Algorithm { get; set; } = CurrentAlgorithm;

    /// <summary>Category name (<see cref="CheckCategory"/>) → weight in the headline score. A missing category weighs 0.</summary>
    public Dictionary<string, int> CategoryWeights { get; set; } = [];

    /// <summary>The enabled checks, ordered by id.</summary>
    public List<ScoredCheck> Checks { get; set; } = [];

    /// <summary>The hash stored as the run's <c>ScoringVersion</c>; see <see cref="ComputeVersion"/>.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Builds a snapshot: disabled checks are left out (they are not run), checks are ordered by id, and
    /// <see cref="Version"/> is computed. The same inputs, in any order, give the same snapshot.
    /// </summary>
    public static ScoringSnapshot Create(IEnumerable<KeyValuePair<CheckCategory, int>> categoryWeights, IEnumerable<ScoredCheck> checks)
    {
        var weights = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (category, weight) in categoryWeights)
            weights[category.ToString()] = Math.Max(0, weight);

        var snapshot = new ScoringSnapshot
        {
            CategoryWeights = weights,
            Checks = checks
                .Where(c => c.Enabled)
                .GroupBy(c => c.CheckId, StringComparer.Ordinal)
                .Select(g => g.First())
                .Select(c => new ScoredCheck { CheckId = c.CheckId, Category = c.Category, Severity = c.Severity, Weight = Math.Max(0, c.Weight), Enabled = true })
                .OrderBy(c => c.CheckId, StringComparer.Ordinal)
                .ToList(),
        };
        snapshot.Version = snapshot.ComputeVersion();
        return snapshot;
    }

    /// <summary>
    /// The first 16 hex digits of a SHA-256 over a canonical form: the algorithm, the category weights ordered by
    /// name, each check's id, category, severity and weight ordered by id. Independent of input order and culture.
    /// </summary>
    public string ComputeVersion()
    {
        var canonical = new StringBuilder();
        canonical.Append("algorithm=").Append(Algorithm.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var (category, weight) in CategoryWeights.OrderBy(w => w.Key, StringComparer.Ordinal))
            canonical.Append("category ").Append(category).Append('=').Append(weight.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var check in Checks.OrderBy(c => c.CheckId, StringComparer.Ordinal))
            canonical.Append("check ").Append(check.CheckId).Append(' ').Append(check.Category).Append(' ').Append(check.Severity)
                .Append(' ').Append(check.Weight.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))[..16];
    }

    /// <summary>The weight of <paramref name="category"/> in the headline score (0 when the snapshot has none).</summary>
    public int WeightOf(CheckCategory category) => CategoryWeights.TryGetValue(category.ToString(), out var weight) ? weight : 0;
}

/// <summary>One check as a run scored it.</summary>
public sealed class ScoredCheck
{
    public string CheckId { get; set; } = string.Empty;
    public CheckCategory Category { get; set; }
    public CheckSeverity Severity { get; set; }
    public int Weight { get; set; }

    /// <summary>Only an input to <see cref="ScoringSnapshot.Create"/>: a snapshot holds enabled checks only.</summary>
    public bool Enabled { get; set; } = true;
}
