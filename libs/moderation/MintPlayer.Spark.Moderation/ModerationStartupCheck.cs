using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Authorization;

namespace MintPlayer.Spark.Moderation;

/// <summary>
/// Validates the <b>layered</b> moderation configuration against <c>security.json</c> at startup
/// (#460, D12, D14). Every problem is collected, then startup is refused with all of them.
/// </summary>
internal static class ModerationStartupCheck
{
    public static void Run(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<SparkModerationOptions>>().Value;
        var security = services.GetRequiredService<ISecurityConfigurationLoader>().GetConfiguration();
        var problems = Validate(options, security);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "Spark Moderation refused to start (Spark:Moderation / moderation.json):" + Environment.NewLine
                + string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));
        }

        services.GetService<ILoggerFactory>()?.CreateLogger("MintPlayer.Spark.Moderation")
            .LogInformation("Moderation: {Count} privilege(s) configured: {Privileges}.",
                options.Privileges.Count,
                string.Join(", ", options.Privileges.Select(p => $"{p.Key} (rep {p.Value.Rep}, age {p.Value.MinAccountAgeDays}d, active {p.Value.MinActiveDays}d)")));
    }

    /// <summary>The problems with <paramref name="options"/>; empty when it is valid.</summary>
    public static IReadOnlyList<string> Validate(SparkModerationOptions options, SecurityConfiguration security)
    {
        var problems = new List<string>();

        foreach (var name in options.Reputation.Keys)
        {
            if (!ReputationEventKinds.Configurable.Contains(name))
                problems.Add($"Reputation event '{name}' is unknown. Known events: {string.Join(", ", ReputationEventKinds.Configurable.Order())}.");
        }

        foreach (var action in options.Earnable)
        {
            if (ModerationRights.NeverEarnable.Contains(action))
                problems.Add($"Earnable lists '{action}', which is never earnable (destructive or reaches hidden rows): {string.Join(", ", ModerationRights.NeverEarnable.Order())}.");
        }

        var earnable = new HashSet<string>(ModerationRights.DefaultEarnable, StringComparer.OrdinalIgnoreCase);
        earnable.UnionWith(options.Earnable.Where(a => !ModerationRights.NeverEarnable.Contains(a)));

        var declared = new HashSet<Guid>(security.Groups.Keys.Select(k => Guid.TryParse(k, out var g) ? g : Guid.Empty));
        var wellKnown = new HashSet<Guid>((security.WellKnown ?? []).Values.Select(v => Guid.TryParse(v, out var g) ? g : Guid.Empty));

        foreach (var (name, privilege) in options.Privileges)
        {
            if (privilege.GroupId == Guid.Empty)
            {
                problems.Add($"Privilege '{name}' has no GroupId.");
                continue;
            }
            if (!declared.Contains(privilege.GroupId))
                problems.Add($"Privilege '{name}' names group {privilege.GroupId}, which security.json does not declare.");
            if (wellKnown.Contains(privilege.GroupId))
                problems.Add($"Privilege '{name}' names group {privilege.GroupId}, which is a well-known group (anonymous/authenticated are decided by sign-in state, never earned).");
            if (privilege.Rep < 0 || privilege.MinAccountAgeDays < 0 || privilege.MinActiveDays < 0)
                problems.Add($"Privilege '{name}': Rep, MinAccountAgeDays and MinActiveDays must not be negative.");

            var grants = 0;
            foreach (var right in security.Rights.Where(r => r.GroupId == privilege.GroupId && !r.IsDenied))
            {
                grants++;
                var pattern = ResourcePattern.Parse(right.Resource);
                foreach (var expanded in SparkCombinedActions.Expand(pattern.Action))
                {
                    var action = Canonical(expanded, earnable);
                    if (ModerationRights.NeverEarnable.Contains(action))
                        problems.Add($"Privilege '{name}': its group is granted '{right.Resource}', and '{action}' is never earnable.");
                    else if (!earnable.Contains(action))
                        problems.Add($"Privilege '{name}': its group is granted '{right.Resource}', and '{action}' is not earnable. Add it to Spark:Moderation:Earnable if a reputation-earned group really may hold it.");
                }
            }
            if (grants == 0)
                problems.Add($"Privilege '{name}': group {privilege.GroupId} has no grant in security.json, so earning it changes nothing. Run with --spark-init-moderation for the grants to add.");
        }

        var fraud = options.Fraud;
        if (fraud.DiversityVotersDivisor <= 0 || fraud.DiversityDaysDivisor <= 0)
            problems.Add("Fraud: DiversityVotersDivisor and DiversityDaysDivisor must be positive.");
        if (fraud.CreditDelayHours < 0 || fraud.DetectorWindowDays <= 0 || fraud.IpKeyRotationDays <= 0 || fraud.IpObservationRetentionDays <= 0)
            problems.Add("Fraud: CreditDelayHours must not be negative; DetectorWindowDays, IpKeyRotationDays and IpObservationRetentionDays must be positive.");
        if (fraud.ConcentrationPercent is <= 0 or > 100)
            problems.Add("Fraud: ConcentrationPercent must be between 1 and 100.");

        return problems;
    }

    /// <summary>The spelling of <paramref name="action"/> the lists use (resources are upper-cased on parse).</summary>
    private static string Canonical(string action, IEnumerable<string> earnable)
        => ModerationRights.NeverEarnable.Concat(earnable).Concat(["Delete"])
               .FirstOrDefault(a => string.Equals(a, action, StringComparison.OrdinalIgnoreCase))
           ?? (action.Length == 0 ? action : char.ToUpperInvariant(action[0]) + action[1..].ToLowerInvariant());
}
