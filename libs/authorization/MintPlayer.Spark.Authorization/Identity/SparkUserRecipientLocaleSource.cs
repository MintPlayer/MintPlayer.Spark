using Microsoft.AspNetCore.Identity;
using MintPlayer.Spark.Abstractions.DateTimes;
using MintPlayer.Spark.Authorization.Endpoints.Account;

namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>
/// The account side of <see cref="ISparkRecipientLocale"/> (commerce PRD X14): <see cref="SparkUser.PreferredTimeZone"/>
/// and <see cref="SparkUser.PreferredCulture"/>, read for mails, PDFs and jobs, and filled from the browser where unset.
/// </summary>
internal sealed class SparkUserRecipientLocaleSource<TUser>(UserManager<TUser> users) : ISparkRecipientLocaleSource
    where TUser : SparkUser
{
    public async Task<SparkRecipientLocalePreference?> GetAsync(string userId, CancellationToken cancellationToken)
        => await users.FindByIdAsync(userId) is { } user
            ? new SparkRecipientLocalePreference(user.PreferredTimeZone, user.PreferredCulture)
            : null;

    public async Task SaveIfUnsetAsync(string userId, SparkRecipientLocale locale, CancellationToken cancellationToken)
    {
        if (await users.FindByIdAsync(userId) is not { } user)
            return;

        var changed = false;
        if (string.IsNullOrEmpty(user.PreferredTimeZone) && SparkTimeZoneIds.IsWellFormed(locale.TimeZoneId))
        {
            user.PreferredTimeZone = locale.TimeZoneId;
            changed = true;
        }
        if (string.IsNullOrEmpty(user.PreferredCulture) && SparkAccount.TryNormalizeCulture(locale.Culture, out var culture) && culture is not null)
        {
            user.PreferredCulture = culture;
            changed = true;
        }

        if (changed)
            await users.UpdateAsync(user);
    }
}
