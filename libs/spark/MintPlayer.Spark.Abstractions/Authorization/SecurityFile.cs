using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Abstractions.Authorization;

/// <summary>
/// The shape of one <c>security.json</c> layer as written: the application's file or a library's
/// (composition D4). It describes the file for the JSON schema; the run time composes the layers on
/// the raw JSON (<see cref="SparkSecurityFiles"/>) into a <see cref="SecurityConfiguration"/>.
/// </summary>
public sealed class SecurityFile
{
    /// <summary>Group id → untranslated name. The application's only (<see cref="SecurityConfiguration.Groups"/>).</summary>
    public Dictionary<string, string> Groups { get; set; } = new();

    /// <summary>The group ids of <c>@anonymous</c> and <c>@authenticated</c>. The application's only (<see cref="SecurityConfiguration.WellKnown"/>).</summary>
    public Dictionary<string, string>? WellKnown { get; set; }

    /// <summary>Library slot (<c>alias:slot</c>) → the group ids or names it stands for. The application's only.</summary>
    public Dictionary<string, List<string>>? Bindings { get; set; }

    /// <summary>Library alias → <c>false</c> to switch off every right it ships. The application's only.</summary>
    public Dictionary<string, bool>? Libraries { get; set; }

    /// <summary>
    /// Targets a library owns that no model file declares (a pseudo-type such as <c>Moderation</c>),
    /// so it may grant on them. A library's only; one that names a model type is refused.
    /// </summary>
    public List<string>? ReservedTargets { get; set; }

    /// <summary>The rights this layer states, keyed.</summary>
    public List<SecurityFileRight> Rights { get; set; } = new();
}

/// <summary>One element of <c>rights</c> in a <c>security.json</c> layer.</summary>
public sealed class SecurityFileRight
{
    /// <summary>
    /// The stable key. The application's: any text without <c>:</c> (its old ids are fine). A
    /// library's: written bare (<c>passkeys-read</c>) and namespaced by its alias when composed. The
    /// application names a library grant only to remove it: <c>"authorization:passkeys-read"</c> with
    /// <c>"$remove": true</c>.
    /// </summary>
    public required string Key { get; set; }

    /// <summary><c>true</c> removes the grant a lower layer ships under <see cref="Key"/>; nothing else may be stated beside it.</summary>
    [JsonPropertyName("$remove")]
    public bool Remove { get; set; }

    /// <summary><c>{Action}/{Target}</c> or <c>{verb}/{Type}/{Attribute}</c> (<see cref="Right.Resource"/>).</summary>
    public string Resource { get; set; } = string.Empty;

    /// <summary>
    /// A group id, <c>@anonymous</c>, <c>@authenticated</c>, or a library slot (<c>moderation:moderators</c>)
    /// the application binds in <c>bindings</c>. A library names tokens and its own slots only.
    /// </summary>
    public string GroupId { get; set; } = string.Empty;

    /// <summary>Denies instead of granting (<see cref="Right.IsDenied"/>). Refused in a library layer.</summary>
    public bool IsDenied { get; set; }

    /// <summary>The precedence tier (<see cref="Right.IsImportant"/>). Refused in a library layer.</summary>
    public bool IsImportant { get; set; }
}
