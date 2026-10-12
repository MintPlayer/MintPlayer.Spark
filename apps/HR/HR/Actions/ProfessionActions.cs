using HR.Entities;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;

namespace HR.Actions;

/// <summary>
/// Exists to state this type's row policy, and nothing else.
/// <para>
/// Visitors and signed-in users hold <c>Read/Profession</c> because they read companies, and since MCP
/// PRD D19 (owner decision 2026-10-12) a reference — <c>Company.Sector</c> — is shown only to a caller who
/// may <c>Read</c> its target type. A grant to a well-known role makes the row filter the only thing
/// between the caller and the collection, so its absence has to be written down.
/// </para>
/// </summary>
public class ProfessionActions : DefaultPersistentObjectActions<Profession>, ISparkOwnsRowSecurity
{
    public ProfessionActions(IEntityMapper entityMapper) : base(entityMapper) { }

    /// <inheritdoc />
    public string RowSecurityRationale =>
        "Professions are public reference data (a description and a regime), the sector a company is filed under; nothing in them is about a person.";
}
