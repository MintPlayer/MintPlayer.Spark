using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Authorization.Models;

/// <summary>
/// Root model for the security.json configuration file.
/// </summary>
public class SecurityConfiguration
{
    /// <summary>
    /// Map of group ID (GUID string) to group name.
    /// Example: { "a76a9b99-225d-4b3c-8985-cd29a9ddbd4e": {"en": "Admins"} }
    /// </summary>
    public Dictionary<string, TranslatedString> Groups { get; set; } = new();

    /// <summary>
    /// Optional descriptions for groups.
    /// Key is the group ID, value is a human-readable description.
    /// </summary>
    public Dictionary<string, TranslatedString>? GroupComments { get; set; }

    /// <summary>
    /// List of rights (permissions) that grant or deny access to resources.
    /// </summary>
    public List<Right> Rights { get; set; } = new();
}
