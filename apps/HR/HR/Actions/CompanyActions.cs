using HR.Entities;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;

namespace HR.Actions;

/// <summary>
/// Exists to state this type's row policy, and nothing else.
/// <para>
/// Company is granted to a well-known role in security.json, which means the row filter is the only
/// thing between the caller and the whole collection — so the framework requires the absence of one
/// to be a decision somebody wrote down rather than a gap nobody noticed.
/// </para>
/// </summary>
public class CompanyActions : DefaultPersistentObjectActions<Company>, ISparkOwnsRowSecurity
{
    public CompanyActions(IEntityMapper entityMapper) : base(entityMapper) { }

    /// <inheritdoc />
    public string RowSecurityRationale =>
        "Companies are the tenant boundary rather than something inside it — the same reasoning as the Fleet demo. Listing company names to a signed-in HR user is intended; the people underneath are a separate grant.";
}
