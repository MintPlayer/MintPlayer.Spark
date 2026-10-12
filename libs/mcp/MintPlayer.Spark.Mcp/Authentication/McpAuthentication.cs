using System.Security.Claims;

namespace MintPlayer.Spark.Mcp.Authentication;

/// <summary>
/// One wired-up way for agents to present a token on <c>/mcp</c> (MCP PRD D4): the bearer scheme that
/// validates it and what the call learns about the agent from the principal it produces.
/// </summary>
/// <param name="Setup">Which of the three setups this is.</param>
/// <param name="Scheme">The ASP.NET Core authentication scheme that validates the token. Only these run on <c>/mcp</c>; cookies never do.</param>
/// <param name="Describe">The agent behind a principal this scheme produced: kind, client id, verified host.</param>
/// <param name="SubjectIsLocalUserId">Whether the token's <c>sub</c> is the local user id (setups 1 and 3; setup 2 maps through a linked login first).</param>
/// <param name="Challenge">The <c>WWW-Authenticate</c> value a 401 carries, e.g. <c>Bearer resource_metadata="…"</c>; <see langword="null"/> for a setup that offers no OAuth (personal tokens), whose 401 is plain.</param>
internal sealed record McpAuthenticationSetup(
    EAgentAuthentication Setup,
    string Scheme,
    Func<ClaimsPrincipal, AgentInformation> Describe,
    bool SubjectIsLocalUserId,
    string? Challenge);

/// <summary>
/// Recognises whether <see cref="Setup"/> is wired up in the built application, and if so how.
/// </summary>
/// <param name="Setup">The setup recognised.</param>
/// <param name="Requirement">What the application must add for it, named in the startup failure for an explicit flag without it.</param>
/// <param name="Detect">The setup when wired, else <see langword="null"/>. Runs once, at startup, against the built container.</param>
internal sealed record McpAuthenticationDetector(
    EAgentAuthentication Setup,
    string Requirement,
    Func<IServiceProvider, McpAuthenticationSetup?> Detect)
{
    /// <summary>
    /// The three setups, none of which exists yet: personal tokens arrive with the Authorization package's
    /// grants (plan M3a), the embedded server with <c>ForMcp()</c> (M3b), the external server with the resource
    /// server wiring (M3c). Until then every call runs as <c>anonymous</c>, and an explicit flag refuses startup.
    /// </summary>
    public static readonly IReadOnlyList<McpAuthenticationDetector> BuiltIn =
    [
        new(EAgentAuthentication.PersonalAccessToken,
            "personal access tokens from MintPlayer.Spark.Authorization (not available yet)",
            static _ => null),
        new(EAgentAuthentication.ExternalAuthorizationServer,
            "an external authorization server configured as a sign-in provider (not available yet)",
            static _ => null),
        new(EAgentAuthentication.EmbeddedAuthorizationServer,
            "MintPlayer.Spark.IdentityProvider with idp.ForMcp() (not available yet)",
            static _ => null),
    ];
}

/// <summary>
/// The setups in effect: the configured flags ∩ what is wired (MCP PRD D4). Resolved once at startup.
/// </summary>
internal sealed class McpAuthentication
{
    private readonly object gate = new();
    private IReadOnlyList<McpAuthenticationSetup>? setups;

    /// <summary>The effective setups, in the order of <see cref="EAgentAuthentication"/>'s flags.</summary>
    public IReadOnlyList<McpAuthenticationSetup> Setups
        => setups ?? throw new InvalidOperationException("The MCP authentication setups are resolved by UseSpark(); it has not run.");

    /// <summary>Whether a 401 can lead the agent to an OAuth sign-in (setup 2 or 3 wired).</summary>
    public bool OffersOAuth => Setups.Any(s => s.Challenge is not null && s.Setup != EAgentAuthentication.PersonalAccessToken);

    /// <summary>Whether a 401 can be answered by configuring a personal access token.</summary>
    public bool OffersPersonalTokens => Setups.Any(s => s.Setup == EAgentAuthentication.PersonalAccessToken);

    /// <summary>Resolves the effective setups, or throws naming every explicit flag that is not wired.</summary>
    /// <exception cref="InvalidOperationException">An explicitly configured setup is not wired up.</exception>
    public IReadOnlyList<McpAuthenticationSetup> Resolve(IServiceProvider services, SparkMcpOptions options)
    {
        lock (gate)
        {
            if (setups is not null)
                return setups;

            var resolved = new List<McpAuthenticationSetup>();
            var missing = new List<string>();
            foreach (var flag in new[] { EAgentAuthentication.PersonalAccessToken, EAgentAuthentication.ExternalAuthorizationServer, EAgentAuthentication.EmbeddedAuthorizationServer })
            {
                if (!options.Authentication.HasFlag(flag))
                    continue;

                var detectors = options.AuthenticationDetectors.Where(d => d.Setup == flag).ToList();
                var setup = detectors.Select(d => d.Detect(services)).FirstOrDefault(s => s is not null);
                if (setup is not null)
                    resolved.Add(setup);
                else if (options.AuthenticationIsExplicit)
                    missing.Add($"{flag} needs {string.Join(" or ", detectors.Select(d => d.Requirement).Distinct())}");
            }

            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    "mcp.Authentication names setups that are not wired up: " + string.Join("; ", missing) + ". "
                    + "Add the missing packages or configuration, or leave mcp.Authentication at its default (All), which uses whatever is wired.");
            }

            return setups = resolved;
        }
    }
}
