using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// What AI agents connected through MCP may do, as comma-separated names (<c>"Read, Write"</c>). One enum at
/// every layer (MCP PRD D6): the application's <c>AgentPermissions</c>, a type's <c>agentPermissions</c> in
/// its model JSON, and the user's grant. The effective permission is their intersection with the caller's
/// own <c>security.json</c> rights — an agent never does more than the user it acts for.
/// </summary>
[Flags]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EAgentPermission
{
    /// <summary>Not exposed to agents. A type's default: an agent sees only what a model opts in.</summary>
    None = 0,
    /// <summary>Agents may list, query and read it, within the user's rights.</summary>
    Read = 1,
    /// <summary>
    /// Agents may propose changes to it (create, update, delete, custom actions), applied after approval.
    /// Never without <see cref="Read"/>: a type that declares <c>Write</c> alone refuses startup.
    /// </summary>
    Write = 2,
}
