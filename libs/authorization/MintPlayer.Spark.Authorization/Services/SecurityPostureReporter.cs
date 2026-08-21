using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Authorization.Configuration;

namespace MintPlayer.Spark.Authorization.Services;

/// <summary>
/// Computes what an unauthenticated caller can reach, from <c>security.json</c> alone.
/// <para>
/// This is the mirror image of SPARK004. Middleware order is a property of the code and undetectable
/// at runtime, so it ships as an analyzer; the anonymous surface is a property of a hot-reloadable
/// JSON file that is not in the compilation and is not the file that ships, so it is trivially
/// computable at runtime and barely computable at build time. Startup is where it belongs.
/// </para>
/// </summary>
[Register(typeof(ISecurityPostureReporter), ServiceLifetime.Singleton)]
internal partial class SecurityPostureReporter : ISecurityPostureReporter
{
    [Inject] private readonly ISecurityConfigurationLoader configLoader;
    [Inject] private readonly IOptions<AuthorizationOptions> options;

    public SecurityPosture Describe()
    {
        var config = configLoader.GetConfiguration();
        var warnings = new List<string>();

        var anonymousGroupId = ResolveAnonymousGroupId(config);

        // A caller who has not signed in belongs to the anonymous group and to nothing else: group
        // membership otherwise comes from claims, and an unauthenticated principal carries none that
        // resolve. So the anonymous surface is exactly that group's non-denied rights.
        var reachable = anonymousGroupId is null
            ? []
            : config.Rights
                .Where(r => r.GroupId == anonymousGroupId && !r.IsDenied)
                .Select(r => r.Resource)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
                .ToList();

        if (options.Value.DefaultBehavior == DefaultAccessBehavior.AllowAll)
        {
            warnings.Add(
                "AuthorizationOptions.DefaultBehavior is AllowAll, so any right not explicitly denied "
                + "is granted to every caller. The list above is therefore a floor, not a ceiling.");
        }

        return new SecurityPosture(reachable, warnings);
    }

    private static Guid? ResolveAnonymousGroupId(Models.SecurityConfiguration config)
    {
        foreach (var (key, value) in config.WellKnown ?? [])
        {
            if (string.Equals(key, SecurityConfigurationValidator.Anonymous, StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(value, out var id))
            {
                return id;
            }
        }

        return null;
    }
}
