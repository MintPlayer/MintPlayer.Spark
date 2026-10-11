using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;

namespace MintPlayer.Spark.Expiration;

/// <summary>
/// Makes <see cref="IExpiring.ExpiresAtUtc"/> framework-owned on persistent-object writes
/// (<c>docs/seo_analyzer_PRD.md</c> D41), the way SoftDelete owns its fields: whatever the client posted,
/// an edit (or a revert, or a restore) keeps the stored expiry and a creation starts without one, so
/// <see cref="ExpirationStamper"/> applies the model's <c>expiresAfter</c>. A user with <c>Edit</c> on the
/// type can therefore neither extend nor clear a document's expiry.
/// </summary>
/// <remarks>
/// Only server code changes it: a before-save interceptor registered after this one (the framework's
/// are registered first, by <c>AddSpark</c>), or a write through a raw session, which the stamper
/// mirrors into <c>@expires</c>. A <c>Sync</c> never reaches it: the owner module already decided.
/// </remarks>
internal sealed class ExpirationInterceptor : IBeforeSave
{
    public bool AppliesTo(Type entityType) => typeof(IExpiring).IsAssignableFrom(entityType);

    public ValueTask OnBeforeSaveAsync(SaveContext context)
    {
        if (context.Entity is IExpiring entity)
            entity.ExpiresAtUtc = context.Before is IExpiring stored ? stored.ExpiresAtUtc : null;
        return ValueTask.CompletedTask;
    }
}
