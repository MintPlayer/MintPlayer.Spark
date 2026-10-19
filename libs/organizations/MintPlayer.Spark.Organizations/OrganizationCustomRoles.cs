using System.Text.RegularExpressions;

namespace MintPlayer.Spark.Organizations;

/// <summary>
/// Custom organization roles (<c>docs/commerce_PRD.md</c> X8): named roles a host or a library declares
/// (<see cref="SparkOrganizationsOptions.CustomRoles"/>, <c>spark.AddOrganizationRole</c>), held by a member beside
/// their built-in role and named in rights with <c>[RequiresOrganizationRole(action, "scanner")]</c>.
/// </summary>
/// <remarks>
/// Unlike the built-in roles they are not ordered: holding <c>finance</c> says nothing about <c>scanner</c>.
/// An Owner passes every action anyway; an Admin gives and takes custom roles on the membership form.
/// </remarks>
public static partial class OrganizationCustomRoles
{
    /// <summary>A letter, then letters, digits, <c>-</c> or <c>_</c>; at most 64 characters; never a built-in role's name.</summary>
    public static bool IsValidName(string? name)
        => name is not null
            && NamePattern().IsMatch(name)
            && !Enum.GetNames<OrganizationRole>().Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="requested"/> as stored: trimmed, without duplicates, each in its declared spelling, in
    /// declaration order. Returns the names that are not declared in <paramref name="undeclared"/>.
    /// </summary>
    internal static List<string> Normalize(IEnumerable<string?>? requested, IReadOnlyDictionary<string, string> declared, out List<string> undeclared)
    {
        undeclared = [];
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in requested ?? [])
        {
            var name = raw?.Trim();
            if (string.IsNullOrEmpty(name))
                continue;
            if (declared.ContainsKey(name))
                wanted.Add(name);
            else
                undeclared.Add(name);
        }
        return declared.Keys.Where(wanted.Contains).ToList();
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]{0,63}$")]
    private static partial Regex NamePattern();
}
