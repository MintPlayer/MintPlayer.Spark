using MintPlayer.Spark.Mcp.Authentication;

namespace MintPlayer.Spark.Mcp;

/// <summary>
/// The text of a 401 on <c>/mcp</c>, for the agent to act on (MCP PRD D17, spike S11). No client starts its
/// OAuth sign-in when a tool call answers 401 mid-session, so the text names each client's own way to start it.
/// Sending the user to the website's sign-in page would not help: that gives the browser a cookie, and
/// <c>/mcp</c> ignores cookies.
/// </summary>
internal static class McpSignInText
{
    /// <summary>The prefix every refusal of an anonymous call starts with, whatever was asked for.</summary>
    public const string Status = "401 Unauthorized: sign-in required.";

    public static string For(McpAuthentication authentication, string serverName)
    {
        var name = string.IsNullOrWhiteSpace(serverName) ? "<name>" : $"<name> (the name this server was added under, e.g. \"{serverName.ToLowerInvariant()}\")";

        if (authentication.OffersOAuth)
        {
            return $"{Status} This call needs the user to sign this agent in. Start the client's own sign-in: "
                + $"Claude Code: call this server's authenticate tool, or run `claude mcp login {name}` (or /mcp, then Authenticate). "
                + $"Codex: run `codex mcp login {name}`, then reconnect. "
                + $"Gemini CLI: ask the user to run `/mcp auth {name}`. "
                + "Signing in on the website does not help: this endpoint ignores browser cookies.";
        }

        if (authentication.OffersPersonalTokens)
        {
            return $"{Status} This call needs a signed-in user. Ask the user to create a personal access token on their account page "
                + "and configure it for this server as an `Authorization: Bearer` header, then reconnect.";
        }

        return $"{Status} This server offers agents no sign-in; only what anonymous visitors may see is available.";
    }
}
