namespace MintPlayer.Spark.Abstractions.Authorization;

/// <summary>
/// Root model for the <c>App_Data/security.json</c> configuration file.
/// <para>
/// Every Spark application has one. It is not optional and there is no code-level way to opt out:
/// a missing or malformed file refuses startup, in the same shape as the model-hash gate, rather
/// than degrading into a permissive default that looks like it is working.
/// </para>
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
    /// Declares which group id plays each of Spark's well-known roles. The only recognised keys are
    /// <c>anonymous</c> — a caller who has not signed in — and <c>authenticated</c>, every caller
    /// who has, whatever claims they carry. See <see cref="SparkWellKnownGroups"/>.
    /// <code>
    /// "wellKnown": {
    ///   "anonymous":     "00000000-0000-0000-0000-000000000000",
    ///   "authenticated": "a1b2c3d4-0000-0000-0000-00000000000f"
    /// }
    /// </code>
    /// <para>
    /// <b>By id, not by name.</b> These used to be matched against a group's <em>display name</em>
    /// through <c>TranslatedString.GetDefaultValue()</c>, which returns the first translation in
    /// <em>file order</em> — not the English one. So <c>{"en":"Everyone","nl":"Iedereen"}</c> matched
    /// and <c>{"nl":"Iedereen","en":"Everyone"}</c> did not: reordering two JSON keys silently
    /// changed who could reach what. Meanwhile membership resolution matched a claim against
    /// <em>any</em> translation — two different rules, sixty lines apart. An explicit id map fixes
    /// localization, renaming, duplicates and case at once.
    /// </para>
    /// <para>
    /// It also makes the roles unassertable. A group id declared here is excluded from claim-based
    /// membership resolution, so no <c>IGroupMembershipProvider</c> — including a custom one reading
    /// an external identity provider's claims — can hand a caller the <c>authenticated</c> group by
    /// naming it.
    /// </para>
    /// <para>
    /// Optional: an application declaring neither simply has neither, and then grants only ever
    /// reach callers whose claims resolve to a declared group.
    /// </para>
    /// </summary>
    public Dictionary<string, string>? WellKnown { get; set; }

    /// <summary>
    /// List of rights (permissions) that grant or deny access to resources.
    /// </summary>
    public List<Right> Rights { get; set; } = new();
}
