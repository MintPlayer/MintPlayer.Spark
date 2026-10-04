namespace MintPlayer.Spark.History;

/// <summary>
/// The <c>security.json</c> rights the History package asks for, per entity type.
/// </summary>
/// <remarks>
/// Lives in this assembly, next to its <c>[assembly: SparkReservedActions(typeof(HistoryRights))]</c>
/// (<c>AssemblyInfo.cs</c>), not in History.Abstractions with <see cref="IAuditable"/> (#388): the
/// reserved-action scan reads the attribute off the assembly that declares it.
/// </remarks>
public static class HistoryRights
{
    /// <summary><c>History/T</c>: list a row's revisions and read one.</summary>
    public const string History = "History";

    /// <summary><c>Revert/T</c> (together with <c>Edit/T</c>): save a row back to one of its revisions.</summary>
    public const string Revert = "Revert";
}

/// <summary>
/// Resolves user ids to display names for revision lists (#460, D8: names at read time). Optional —
/// without one, a revision carries the id only. Register one with
/// <c>AddHistoryUserNameResolver&lt;T&gt;()</c>; an app on Spark's Identity typically looks the ids up
/// in its user store.
/// </summary>
public interface IHistoryUserNameResolver
{
    /// <summary>
    /// A name per id it knows. An id missing from the result is shown as a deleted user. Called once
    /// per revision list, with every distinct id on the page.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ResolveAsync(IReadOnlyCollection<string> userIds, CancellationToken cancellationToken);
}
