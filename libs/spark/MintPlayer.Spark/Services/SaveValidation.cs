using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Validation of an endpoint's save on the values the save will keep (contributions M2d).
/// </summary>
/// <remarks>
/// <para>
/// The <c>po/create</c> and <c>po/update</c> endpoints used to validate the posted object before
/// calling <see cref="IDatabaseAccess"/>, which is where the write shield drops what the caller may
/// not write. So a <i>required</i> attribute the caller cannot write — blanked by the per-row hook and
/// posted back empty, or refused by a static <c>Edit</c> right — failed "required" although the save
/// would have kept its stored value: an error the user can neither see the reason for nor fix.
/// </para>
/// <para>
/// The endpoint now <see cref="Request"/>s validation of its object, and
/// <see cref="IDatabaseAccess"/> runs it right after the shield — after every gate, before the
/// interceptors and the write. Attributes the shield reports as
/// <see cref="AttributeWriteShieldResult.Unwritable"/> are <b>not validated</b>: the value that is kept
/// is the stored one (or the CLR default on a create), not the caller's. Validating the stored value
/// instead was rejected: a rule failing on a value the caller may not see (a <c>maxLength</c>, a
/// <c>regex</c>) would answer a question about it — the oracle the shield exists to close — and
/// would still be an error the caller cannot fix.
/// </para>
/// <para>
/// The refresh hook still shapes the rules (see <see cref="IRefreshInvoker.BuildEffectiveAsync"/>),
/// and sees the effective values: the caller's for what they may write, the stored ones for what
/// they may not. Nothing it sees is returned — only the errors for writable attributes, whose
/// messages name the attribute and the rule, never a value.
/// </para>
/// <para>
/// Keyed to the object instance, so a save an interceptor or an Actions class makes during the same
/// request is not validated by the endpoint's request.
/// </para>
/// </remarks>
internal interface ISaveValidation
{
    /// <summary>Asks the save of exactly <paramref name="persistentObject"/> to be validated.</summary>
    void Request(PersistentObject persistentObject, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates <paramref name="shielded"/> when its save was requested, and consumes the request.
    /// Throws <see cref="SparkSaveValidationException"/> with every error.
    /// </summary>
    Task ValidateAsync(
        PersistentObject shielded, EntityTypeDefinition definition, object? stored, IReadOnlySet<string> unwritable);
}

/// <summary>The errors of a save validated by <see cref="ISaveValidation"/>; endpoints answer 400.</summary>
internal sealed class SparkSaveValidationException(ValidationResult result) : Exception("The persistent object is not valid.")
{
    public ValidationResult Result { get; } = result;
}

[Register(typeof(ISaveValidation), ServiceLifetime.Scoped)]
internal sealed partial class SaveValidation : ISaveValidation
{
    [Inject] private readonly IRefreshInvoker refreshInvoker;
    [Inject] private readonly IValidationService validationService;
    [Inject] private readonly IEntityMapper entityMapper;

    private PersistentObject? requested;
    private CancellationToken requestAborted;

    public void Request(PersistentObject persistentObject, CancellationToken cancellationToken = default)
    {
        requested = persistentObject;
        requestAborted = cancellationToken;
    }

    public async Task ValidateAsync(
        PersistentObject shielded, EntityTypeDefinition definition, object? stored, IReadOnlySet<string> unwritable)
    {
        if (!ReferenceEquals(requested, shielded))
            return;
        requested = null;

        // The model's own write gate (EntityMapper.IsWritableBySchema) drops a read-only attribute's
        // posted value, so it is no more the caller's to supply than a rights-denied one: a
        // server-stamped required field (ApiToken.CreatedAtUtc) failed "required" on every create.
        // ⚠️ Read-only only, NOT hidden: a refresh hook can make a model-hidden attribute visible and
        // required (RefreshEndpointTests' PoliceReport), and skipping it would let a client that never
        // refreshed escape the rule.
        unwritable = new HashSet<string>(
            [.. unwritable, .. definition.Attributes.Where(a => a.IsReadOnly).Select(a => a.Name)],
            StringComparer.Ordinal);

        // What the refresh hook is handed: the caller's values for what they may write, the stored
        // values for what they may not (the CLR default on a create — the scaffold's empty value).
        var storedObject = stored is not null && unwritable.Count > 0 ? entityMapper.ToPersistentObject(stored, definition.Id) : null;
        var submitted = new PersistentObject
        {
            Id = shielded.Id,
            Name = shielded.Name,
            ObjectTypeId = shielded.ObjectTypeId,
            Attributes =
            [
                .. shielded.Attributes
                    .Where(a => !unwritable.Contains(a.Name))
                    .Select(a => new PersistentObjectAttribute { Name = a.Name, Value = a.Value, IsValueChanged = a.IsValueChanged }),
                .. (storedObject?.Attributes ?? [])
                    .Where(a => unwritable.Contains(a.Name))
                    .Select(a => new PersistentObjectAttribute { Name = a.Name, Value = a.Value }),
            ],
        };

        var effective = await refreshInvoker.BuildEffectiveAsync(definition, submitted, requestAborted);
        effective.RetainAttributes(a => !unwritable.Contains(a.Name));

        var result = validationService.ValidateEffective(effective);
        if (!result.IsValid)
            throw new SparkSaveValidationException(result);
    }
}
