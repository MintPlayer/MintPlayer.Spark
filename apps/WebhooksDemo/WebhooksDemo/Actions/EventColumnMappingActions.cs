using WebhooksDemo.Entities;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;

namespace WebhooksDemo.Actions;

/// <summary>
/// Exists to state this type's row policy, and nothing else.
/// <para>
/// EventColumnMapping is granted to a well-known role in security.json, which means the row filter is the only
/// thing between the caller and the whole collection — so the framework requires the absence of one
/// to be a decision somebody wrote down rather than a gap nobody noticed.
/// </para>
/// </summary>
public class EventColumnMappingActions : DefaultPersistentObjectActions<EventColumnMapping>, ISparkOwnsRowSecurity
{
    public EventColumnMappingActions(IEntityMapper entityMapper) : base(entityMapper) { }

    /// <inheritdoc />
    public string RowSecurityRationale =>
        "Configuration rather than data: a mapping says which webhook event moves a card to which column. It names no user and carries nothing per-caller, so there is nothing for a row filter to narrow.";
}
