namespace MintPlayer.Spark.Abstractions.Authorization;

/// <summary>
/// Rights that follow from other rights, applied once while <c>security.json</c> is composed into
/// the rights index — never while a decision is being made, so evaluation stays a set lookup.
/// <para>
/// Distinct from <see cref="SparkCombinedActions"/>, which expands one written name into the
/// several actions it <em>is</em> (<c>QueryRead</c> → <c>Query</c> + <c>Read</c>). This expands an
/// action into what it <em>entails</em>.
/// </para>
/// <para>
/// Every expansion of the rights file must consult this — the evaluator's index and the anonymous
/// posture report both do. A second expansion that skipped it would report a smaller surface than
/// the one actually served, which is the one way that baseline can lie.
/// </para>
/// </summary>
public static class SparkRightImplications
{
    /// <summary>
    /// The actions a <b>granted</b> <paramref name="action"/> also grants, excluding itself. Empty
    /// for everything that entails nothing.
    /// <para>
    /// <c>Read</c> ⇒ <c>Query</c>: a caller who may open every row individually may list them — a
    /// grid discloses nothing that reading the rows one by one would not — and the type catalogue
    /// every page renders from is <c>Query</c>-scoped, so withholding it only blanks a page the
    /// caller is allowed to see. Vidyano encodes the same rule by offering <c>Query</c>,
    /// <c>QueryRead</c>, <c>QueryReadEdit</c> … as bundles and never a bare <c>Read</c>.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <b>Grants only, and one way.</b> The caller decides: pass a denial's actions here and the
    /// result would take away a right the file never denied — "list, but no click-through" is
    /// <c>Query</c> without <c>Read</c>, and a denied <c>Read</c> must leave it expressible.
    /// </remarks>
    public static IReadOnlyList<string> Implied(string action)
        => string.Equals(action, Read, StringComparison.OrdinalIgnoreCase)
            ? [Query]
            : [];

    private const string Read = "Read";
    private const string Query = "Query";
}
