using MintPlayer.Spark.Abstractions.Consent;

namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>
/// The personal-data export's <c>consent</c> section (GDPR Art. 15/20; commerce PRD X21): the account's consent
/// decisions and their history, and those of its e-mail address. Nothing when the consent primitive is not registered.
/// </summary>
internal sealed class SparkConsentPersonalData<TUser>(ISparkConsentPersonalData? consent = null) : ISparkPersonalDataContributor<TUser>
    where TUser : SparkUser
{
    public string Name => "consent";

    public Task<object?> GetPersonalDataAsync(TUser user, CancellationToken cancellationToken)
        => consent is not null && user.Id is { Length: > 0 } userId
            ? consent.ExportAsync(userId, user.Email, cancellationToken)
            : Task.FromResult<object?>(null);
}

/// <summary>Deleting an account erases its consent records and its e-mail address's (GDPR Art. 17; X21). Idempotent.</summary>
internal sealed class SparkConsentAccountDeletion<TUser>(ISparkConsentPersonalData? consent = null) : ISparkAccountDeletionHandler<TUser>
    where TUser : SparkUser
{
    public Task OnDeletingAccountAsync(TUser user, CancellationToken cancellationToken)
        => consent is not null && user.Id is { Length: > 0 } userId
            ? consent.EraseAsync(userId, user.Email, cancellationToken)
            : Task.CompletedTask;
}
