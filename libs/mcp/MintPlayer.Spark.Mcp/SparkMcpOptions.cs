using System.Text;
using Microsoft.Extensions.Configuration;
using MintPlayer.Spark.Abstractions;
using ModelContextProtocol.Server;

namespace MintPlayer.Spark.Mcp;

/// <summary>
/// The MCP server of a Spark application (<c>spark.AddMcp(mcp =&gt; …)</c>, <c>docs/guide-mcp.md</c>). Bound
/// from <c>Spark:Mcp</c> first; what the callback sets wins.
/// </summary>
public sealed class SparkMcpOptions
{
    /// <summary>The configuration section the options are bound from.</summary>
    public const string SectionName = "Spark:Mcp";

    private EAgentAuthentication authentication = EAgentAuthentication.All;
    private string? toolPrefix;

    /// <summary>The server name agents see (<c>serverInfo.name</c>). Also the default of <see cref="ToolPrefix"/>.</summary>
    public string ServerName { get; set; } = "Spark";

    /// <summary>
    /// What every tool name starts with, joined by <c>_</c>: <c>contracts</c> gives <c>contracts_query</c>
    /// (MCP PRD D21). Defaults to a slug of <see cref="ServerName"/>. <b>Set it explicitly before you publish</b>:
    /// users allow-list tools by name, so renaming <see cref="ServerName"/> must not rename them. An empty
    /// string leaves the tools unprefixed.
    /// </summary>
    public string ToolPrefix
    {
        get => toolPrefix ?? Slug(ServerName);
        set => toolPrefix = value;
    }

    /// <summary>The server instructions sent at <c>initialize</c>: what the data is and how the tools fit together.</summary>
    public string? Instructions { get; set; }

    /// <summary>
    /// The setups agents may sign in through (MCP PRD D4). Effective = these flags ∩ what is wired up. Setting a
    /// flag explicitly whose packages or configuration are missing refuses startup; the default,
    /// <see cref="EAgentAuthentication.All"/>, silently uses whatever is wired.
    /// </summary>
    public EAgentAuthentication Authentication
    {
        get => authentication;
        set
        {
            authentication = value;
            AuthenticationIsExplicit = true;
        }
    }

    /// <summary>
    /// Whether <see cref="Authentication"/> was set by the application (its configuration key, or code) rather than left
    /// at its default. <see cref="Create"/> resets it after binding the configuration: see there.
    /// </summary>
    internal bool AuthenticationIsExplicit { get; private set; }

    /// <summary>
    /// <c>Spark:Mcp</c> from <paramref name="configuration"/>, then <paramref name="configure"/> (code wins). A member
    /// counts as set only when the section names it or the callback sets it.
    /// </summary>
    /// <remarks>
    /// The configuration binder calls every public setter, with the getter's own value when the key is absent (so a
    /// setter can adjust it). Binding any <c>Spark:Mcp</c> key therefore marked <see cref="Authentication"/> explicit, so an
    /// application that configured only <c>Spark:Mcp:CanonicalUrl</c> had to wire all three setups or not start; and it
    /// pinned <see cref="ToolPrefix"/> to the slug of the default <see cref="ServerName"/> before the callback named the
    /// server. Both are undone here for keys the section does not hold (found twice on 2026-10-12, adopting CodeCoverage
    /// in plan M6 and wiring Fleet and HR; the two fixes are this one).
    /// </remarks>
    internal static SparkMcpOptions Create(IConfiguration? configuration, Action<SparkMcpOptions>? configure)
    {
        var options = new SparkMcpOptions();
        if (configuration?.GetSection(SectionName) is { } section && section.Exists())
        {
            section.Bind(options);
            options.AuthenticationIsExplicit = section[nameof(Authentication)] is not null;
            if (section[nameof(ToolPrefix)] is null)
                options.toolPrefix = null;
        }
        configure?.Invoke(options);
        return options;
    }

    /// <summary>
    /// The application's layer of agent permissions (MCP PRD D6), evaluated on every call. The effective
    /// permission is this ∩ the type's <c>agentPermissions</c> ∩ the user's grant ∩ <c>security.json</c>. Keep it
    /// fast and free of database calls. An anonymous caller is clamped to <see cref="EAgentPermission.Read"/>
    /// whatever this returns. Default: <see cref="EAgentPermission.Read"/> for everyone.
    /// </summary>
    /// <remarks>
    /// A function rather than a value-or-function wrapper: C# cannot convert a lambda to a class through an
    /// implicit operator (CS1660), so a constant is written <c>_ =&gt; EAgentPermission.Read</c>.
    /// </remarks>
    public Func<AgentInformation, EAgentPermission> AgentPermissions { get; set; } = static _ => EAgentPermission.Read;

    /// <summary>The endpoint path (MCP PRD D20). Startup fails if another endpoint already answers it.</summary>
    public string Path { get; set; } = "/mcp";

    /// <summary>
    /// The canonical server URL: the token audience and what users paste into their agent. Default:
    /// <c>Spark:Auth:PublicBaseUrl</c> + <see cref="Path"/>. Set it (<c>Spark:Mcp:CanonicalUrl</c>) to serve the
    /// endpoint from a subdomain through a proxy. Never derived from the request's <c>Host</c> outside Development.
    /// </summary>
    public string? CanonicalUrl { get; set; }

    /// <summary>
    /// Every request without a token, <c>initialize</c> included, answers 401 with the sign-in challenge, so an
    /// agent signs the user in as soon as it connects (MCP PRD D17, spike S11). Default <see langword="false"/>.
    /// </summary>
    public bool RequireSignIn { get; set; }

    /// <summary>
    /// How long an applied (or failed) plan is kept as the record of what agents changed (MCP PRD D8,
    /// <c>Spark:Mcp:PlanRetention</c>). Default one year. A proposed plan that is never applied expires after
    /// 10 minutes whatever this says.
    /// </summary>
    public TimeSpan PlanRetention { get; set; } = TimeSpan.FromDays(365);

    /// <summary>Call limits per user (signed in) and per address (anonymous), MCP PRD D16.</summary>
    public SparkMcpQuotaOptions Quota { get; set; } = new();

    /// <summary>The application's own tool classes, registered unprefixed and prefixed here.</summary>
    internal List<Type> ToolTypes { get; } = [];

    /// <summary>
    /// Adds the tools of <typeparamref name="TTools"/>: its <see cref="McpServerToolAttribute"/> methods, written
    /// without the prefix. <c>ReadOnly = true</c> means the tool needs <c>Read</c>; anything else needs <c>Write</c>.
    /// </summary>
    public SparkMcpOptions AddTools<TTools>() where TTools : class
    {
        if (!ToolTypes.Contains(typeof(TTools)))
            ToolTypes.Add(typeof(TTools));
        return this;
    }

    /// <summary>The application's own prompt classes, registered prefixed like the tools.</summary>
    internal List<Type> PromptTypes { get; } = [];

    /// <summary>
    /// Adds the prompts of <typeparamref name="TPrompts"/>: its <see cref="McpServerPromptAttribute"/> methods, written
    /// without the prefix (<c>raise_coverage</c> becomes <c>codecoverage_raise_coverage</c>). A prompt is a message
    /// template the user picks in the agent; it reads no data, so every caller sees it.
    /// </summary>
    public SparkMcpOptions AddPrompts<TPrompts>() where TPrompts : class
    {
        if (!PromptTypes.Contains(typeof(TPrompts)))
            PromptTypes.Add(typeof(TPrompts));
        return this;
    }

    /// <summary><see cref="Path"/> with one leading slash and none trailing.</summary>
    internal string NormalizedPath => "/" + Path.Trim().Trim('/');

    /// <summary><c>Contract Manager 2</c> → <c>contract_manager_2</c>: lower-case ASCII letters and digits, runs of anything else one <c>_</c>.</summary>
    internal static string Slug(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Trim().ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
                builder.Append(c);
            else if (builder.Length > 0 && builder[^1] != '_')
                builder.Append('_');
        }
        return builder.ToString().TrimEnd('_');
    }
}

/// <summary>Calls per minute, per user and per address (MCP PRD D16). A value of 0 or less is unlimited.</summary>
public sealed class SparkMcpQuotaOptions
{
    /// <summary>Calls a signed-in user may make per minute, all agents together. Default 120.</summary>
    public int SignedInPerMinute { get; set; } = 120;

    /// <summary>Calls one address may make per minute without a token. Default 30.</summary>
    public int AnonymousPerMinute { get; set; } = 30;
}
