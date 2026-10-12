using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Mcp.Authentication;

/// <summary>
/// One wired-up way for agents to present a token on <c>/mcp</c> (MCP PRD D4): the registered
/// <see cref="IAgentCredentialScheme"/> and the challenge its 401 carries.
/// </summary>
/// <param name="Source">The package's registration: the scheme that validates the token and describes the agent behind it.</param>
internal sealed record McpAuthenticationSetup(IAgentCredentialScheme Source)
{
    /// <summary>
    /// Whether an agent can sign in through this setup with OAuth (setups 2 and 3): its 401 then carries the RFC 9728
    /// challenge (<see cref="Http.McpProtectedResource"/>). Personal tokens offer none, so their 401 is a plain <c>Bearer</c>.
    /// </summary>
    public bool OffersOAuth => Source.Setup != EAgentAuthentication.PersonalAccessToken;

    /// <summary>Which of the three setups this is.</summary>
    public EAgentAuthentication Setup => Source.Setup;

    /// <summary>The ASP.NET Core authentication scheme that validates the token. Only these run on <c>/mcp</c>; cookies never do.</summary>
    public string Scheme => Source.Scheme;

    /// <summary>Whether the token's <c>sub</c> is the local user id (setups 1 and 3; setup 2 maps through a linked login first).</summary>
    public bool SubjectIsLocalUserId => Source.SubjectIsLocalUserId;
}

/// <summary>
/// The setups in effect: the configured flags ∩ what is wired (MCP PRD D4). Resolved once at startup.
/// </summary>
/// <remarks>
/// <b>What "wired" means.</b> A package that authenticates agents registers an <see cref="IAgentCredentialScheme"/>
/// for its setup (the Authorization package for personal access tokens; the IdentityProvider's <c>ForMcp()</c>
/// for the embedded server, plan M3b; the external server's wiring, M3c). The registrations are the only
/// evidence: no package is recognised by its assembly name.
/// </remarks>
internal sealed class McpAuthentication
{
    /// <summary>What an application adds for each setup, named in the startup failure for an explicit flag without it.</summary>
    private static readonly IReadOnlyDictionary<EAgentAuthentication, string> Requirements = new Dictionary<EAgentAuthentication, string>
    {
        [EAgentAuthentication.PersonalAccessToken] = "MintPlayer.Spark.Authorization (spark.AddAuthentication<TUser>()), which issues personal access tokens",
        [EAgentAuthentication.ExternalAuthorizationServer] = "MintPlayer.Spark.Authorization with spark.AddExternalAgentAuthorizationServer(issuer, loginProvider), the server also configured as a sign-in provider",
        [EAgentAuthentication.EmbeddedAuthorizationServer] = "MintPlayer.Spark.IdentityProvider with spark.AddIdentityProvider(idp => idp.ForMcp())",
    };

    private readonly object gate = new();
    private IReadOnlyList<McpAuthenticationSetup>? setups;

    /// <summary>The effective setups, in the order of <see cref="EAgentAuthentication"/>'s flags.</summary>
    public IReadOnlyList<McpAuthenticationSetup> Setups
        => setups ?? throw new InvalidOperationException("The MCP authentication setups are resolved by UseSpark(); it has not run.");

    /// <summary>The effective setups as flags.</summary>
    public EAgentAuthentication Effective => Setups.Aggregate(EAgentAuthentication.None, (all, s) => all | s.Setup);

    /// <summary>Whether a 401 can lead the agent to an OAuth sign-in (setup 2 or 3 wired).</summary>
    public bool OffersOAuth => Setups.Any(s => s.OffersOAuth);

    /// <summary>
    /// The OAuth setups in the order RFC 9728 lists their servers: the embedded server first (MCP PRD §4.2), then an
    /// external one.
    /// </summary>
    public IEnumerable<McpAuthenticationSetup> OAuthSetups
        => Setups.Where(s => s.OffersOAuth).OrderByDescending(s => s.Setup == EAgentAuthentication.EmbeddedAuthorizationServer);

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

            var registered = services.GetServices<IAgentCredentialScheme>().ToList();
            var resolved = new List<McpAuthenticationSetup>();
            var missing = new List<string>();
            foreach (var flag in new[] { EAgentAuthentication.PersonalAccessToken, EAgentAuthentication.ExternalAuthorizationServer, EAgentAuthentication.EmbeddedAuthorizationServer })
            {
                if (!options.Authentication.HasFlag(flag))
                    continue;

                var schemes = registered.Where(s => s.Setup == flag).ToList();
                if (schemes.Count > 0)
                    resolved.AddRange(schemes.Select(s => new McpAuthenticationSetup(s)));
                else if (options.AuthenticationIsExplicit)
                    missing.Add($"{flag} needs {Requirements[flag]}");
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
