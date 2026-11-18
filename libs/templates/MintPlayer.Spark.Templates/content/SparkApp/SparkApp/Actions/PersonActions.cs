using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using SparkApp.Library.Entities;

namespace SparkApp.Actions;

/// <summary>
/// Person's actions: lifecycle hooks (OnBeforeSaveAsync, OnAfterDeleteAsync, …) and custom actions go here.
/// <para>
/// security.json grants Person to a well-known group, so the row filter is the only thing between a caller and
/// the whole collection. Spark refuses to start until the type says how rows are narrowed: override
/// GetRowFilterAsync (or IsAllowedAsync) to scope them, or keep <see cref="ISparkOwnsRowSecurity"/> and write
/// down why every row may be shown.
/// </para>
/// </summary>
public class PersonActions : DefaultPersistentObjectActions<Person>, ISparkOwnsRowSecurity
{
    public PersonActions(IEntityMapper entityMapper) : base(entityMapper) { }

    /// <inheritdoc />
#if (HasAuth)
    public string RowSecurityRationale =>
        "Sample data: every signed-in user sees every person. Scope the rows before storing anything personal.";
#else
    public string RowSecurityRationale =>
        "Sample data in an app without sign-in: every visitor sees every person. Scope the rows before storing anything personal.";
#endif
}
