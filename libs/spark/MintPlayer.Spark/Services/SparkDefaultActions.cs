namespace MintPlayer.Spark.Services;

/// <summary>
/// The names of the framework's own actions — <c>New</c>, <c>Edit</c> and <c>Delete</c> (#460 D18,
/// #467 D7).
/// </summary>
/// <remarks>
/// <para>
/// Their definitions are not here: the core library ships them in its <c>App_Data/actions.json</c>,
/// the first layer of the composed catalogue, so an application overrides any property of them, or
/// removes one with <c>"Edit": null</c>, exactly as it does a custom action's.
/// </para>
/// <para>
/// ⚠️ Rights are the ordinary ones — <c>New/T</c>, <c>Edit/T</c> and <c>Delete/T</c> — not an action
/// right per name. They run through their own endpoints (<c>/po/new</c>, the edit page, <c>/po/delete-many</c>),
/// never <c>/actions/execute</c>: an <see cref="Actions.ICustomAction"/> class of the same name never runs.
/// </para>
/// </remarks>
public static class SparkDefaultActions
{
    public const string New = "New";
    public const string Edit = "Edit";
    public const string Delete = "Delete";

    /// <summary>The largest selection a row-taking action, built-in or custom, accepts in one request.</summary>
    public const int MaxSelectedItems = 200;

    /// <summary>Whether <paramref name="name"/> is one of the built-in action names (case-insensitive).</summary>
    public static bool IsDefault(string? name)
        => string.Equals(name, New, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, Edit, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, Delete, StringComparison.OrdinalIgnoreCase);
}
