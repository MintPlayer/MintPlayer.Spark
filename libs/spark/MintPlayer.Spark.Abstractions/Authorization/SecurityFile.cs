using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Abstractions.Authorization;

/// <summary>
/// Who may do what: groups and the rights granted or denied to them. The application's file is
/// layered over the libraries' files, which may only grant (and state only <c>rights</c> and
/// <c>reservedTargets</c>).
/// </summary>
/// <remarks>
/// The shape of one <c>security.json</c> layer as written: the application's file or a library's
/// (composition D4). It describes the file for the JSON schema; the run time composes the layers on
/// the raw JSON (<see cref="SparkSecurityFiles"/>) into a <see cref="SecurityConfiguration"/>.
/// </remarks>
public sealed class SecurityFile
{
    /// <summary>
    /// Group id → untranslated name. The name is the group's identity: group claims and
    /// <c>[SparkAuthorize(Group = …)]</c> match it ignoring case; the label users see is the key
    /// <c>security.groups.{name}.label</c>. Application only.
    /// </summary>
    public Dictionary<string, string> Groups { get; set; } = new();

    /// <summary>
    /// The group ids the tokens <c>@anonymous</c> (key <c>anonymous</c>) and <c>@authenticated</c>
    /// (key <c>authenticated</c>) stand for; each must be declared in <c>groups</c>. Application only.
    /// </summary>
    public Dictionary<string, string>? WellKnown { get; set; }

    /// <summary>
    /// Library slot (<c>alias:slot</c>, e.g. <c>moderation:moderators</c>) → the group ids or names
    /// (ignoring case) it stands for; a slot bound to several groups yields one right per group. A slot
    /// a library grants to must be bound here, or startup fails. Application only.
    /// </summary>
    public Dictionary<string, List<string>>? Bindings { get; set; }

    /// <summary>
    /// Library alias → <c>false</c> to switch off every right that library ships. They still appear in
    /// <c>securityPosture.txt</c>, marked inert. Application only.
    /// </summary>
    public Dictionary<string, bool>? Libraries { get; set; }

    /// <summary>
    /// Targets a library owns that no model file declares (a pseudo-type such as <c>Moderation</c>),
    /// so it may grant on them. A library's only; one that names a model type is refused.
    /// </summary>
    public List<string>? ReservedTargets { get; set; }

    /// <summary>
    /// The rights this layer states, keyed by <c>key</c>. Anything not granted is refused. A library
    /// layer may only grant, and only on targets it owns; the application removes a library right with
    /// <c>{"key": "&lt;alias&gt;:&lt;name&gt;", "$remove": true}</c> and never edits one.
    /// </summary>
    public List<SecurityFileRight> Rights { get; set; } = new();
}

/// <summary>One element of <c>rights</c> in a <c>security.json</c> layer: a grant or a denial of a resource to a group.</summary>
public sealed class SecurityFileRight
{
    /// <summary>
    /// The stable key. The application's: any text without <c>:</c> (its old ids are fine). A
    /// library's: written bare (<c>passkeys-read</c>) and namespaced by its alias when composed. The
    /// application names a library grant only to remove it: <c>"authorization:passkeys-read"</c> with
    /// <c>"$remove": true</c>.
    /// </summary>
    public required string Key { get; set; }

    /// <summary>
    /// <c>true</c> removes the right a library ships under <c>key</c> (<c>&lt;alias&gt;:&lt;name&gt;</c>);
    /// nothing else may be stated beside it. Application only.
    /// </summary>
    [JsonPropertyName("$remove")]
    public bool Remove { get; set; }

    /// <summary>
    /// <c>{Action}/{Target}</c> (<c>QueryRead/Car</c>) or <c>{Action}/{Type}/{Attribute}</c>
    /// (<c>Edit/Employee/Salary</c>). The action is <c>Query</c>, <c>Read</c>, <c>New</c>, <c>Edit</c>,
    /// <c>Delete</c>, a combination such as <c>QueryReadEditNewDelete</c>, or a custom action's name.
    /// No wildcards.
    /// </summary>
    public string Resource { get; set; } = string.Empty;

    /// <summary>
    /// A group id from <c>groups</c>, <c>@anonymous</c>, <c>@authenticated</c>, or a library slot
    /// (<c>moderation:moderators</c>) the application binds in <c>bindings</c>. A library names tokens
    /// and its own slots only.
    /// </summary>
    public string GroupId { get; set; } = string.Empty;

    /// <summary>Denies instead of granting. A denial beats any ordinary grant, from any group. Refused in a library layer.</summary>
    public bool IsDenied { get; set; }

    /// <summary>
    /// The precedence tier: an important denial beats everything, an important grant beats any
    /// ordinary denial. For break-glass grants and hard prohibitions only. Refused in a library layer.
    /// </summary>
    public bool IsImportant { get; set; }
}
