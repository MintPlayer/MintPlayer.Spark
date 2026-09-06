using DemoApp.Library.Entities;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;

namespace DemoApp.Actions;

/// <summary>
/// Exists to state this type's row policy, and nothing else.
/// <para>
/// Address is granted to a well-known role in security.json, which means the row filter is the only
/// thing between the caller and the whole collection — so the framework requires the absence of one
/// to be a decision somebody wrote down rather than a gap nobody noticed.
/// </para>
/// </summary>
public class AddressActions : DefaultPersistentObjectActions<Address>, ISparkOwnsRowSecurity
{
    public AddressActions(IEntityMapper entityMapper) : base(entityMapper) { }

    /// <inheritdoc />
    public string RowSecurityRationale =>
        "Demo data, published in full on purpose — see PersonActions. Addresses here are sample records with no owner.";
}
