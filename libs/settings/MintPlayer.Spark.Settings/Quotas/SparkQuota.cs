using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Settings.Quotas;

/// <summary>
/// A per-user quota (<c>docs/seo_analyzer_PRD.md</c> X3): at most <see cref="Limit"/> uses in any
/// <see cref="Window"/> (a sliding window), both read from settings (X9), so an administrator changes them at
/// runtime and configuration supplies the layer beneath.
/// <code>
/// public static class SeoQuotas
/// {
///     public static readonly SparkQuota Audits = new("audits",
///         new SparkSetting&lt;int&gt;("Seo:Quotas:Audits:Limit", 5, "Audits a user may start per window."),
///         new SparkSetting&lt;TimeSpan&gt;("Seo:Quotas:Audits:Window", TimeSpan.FromDays(1), "The audits window."));
/// }
/// spark.AddQuotas(typeof(SeoQuotas));
/// [SparkQuota("audits")] public sealed partial class StartAuditAction : ICustomAction { … }
/// </code>
/// </summary>
/// <param name="Name">The quota's name (<c>[A-Za-z0-9._-]</c>, at most 64), part of the usage document id.</param>
/// <param name="Limit">Uses allowed per window. 0 refuses every use; a negative value means unlimited.</param>
/// <param name="Window">The sliding window. Must be positive.</param>
public sealed partial record SparkQuota(string Name, SparkSetting<int> Limit, SparkSetting<TimeSpan> Window)
{
    internal static bool IsValidName(string name) => NamePattern().IsMatch(name ?? "");

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex NamePattern();
}

/// <summary>
/// Counts each run of this custom action against the named quota (declared with <c>AddQuotas</c>), per signed-in
/// user, else per client IP address. Checked after every authorization gate; a refused run is a <b>429</b> with
/// <c>Retry-After</c> and does not count. A run that is admitted counts even if the action then fails, so a
/// failing action cannot be retried for free.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class SparkQuotaAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>The answer to one use.</summary>
/// <param name="Allowed">Whether the use was admitted (and counted).</param>
/// <param name="Limit">The limit that applied (negative = unlimited).</param>
/// <param name="Remaining">Uses left in the window after this one.</param>
/// <param name="RetryAfter">When refused: how long until the oldest counted use leaves the window.</param>
public sealed record SparkQuotaDecision(bool Allowed, int Limit, int Remaining, TimeSpan? RetryAfter);

/// <summary>A quota's usage for one caller, stored at <c>SparkQuotaUsages/{quota}/{key}</c>; it expires with its last use (X1).</summary>
public sealed class SparkQuotaUsage : IExpiring
{
    public string? Id { get; set; }

    /// <summary>The counted uses inside the window, oldest first (pruned on every write).</summary>
    public List<DateTime> Uses { get; set; } = [];

    public DateTime? ExpiresAtUtc { get; set; }

    internal static string IdFor(string quota, string key) => "SparkQuotaUsages/" + quota + "/" + key;
}
