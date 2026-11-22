using CodeCoverage.Entities;
using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.Services;

/// <summary>
/// The one avatar every page shows for an account (#700 M11, PRD Q15): the owner's <see cref="Account.CustomAvatar"/> when
/// it can be served, otherwise the forge's <see cref="Account.AvatarUrl"/>.
/// </summary>
/// <remarks>
/// The custom avatar's URL is the checked download URL (<see cref="SparkFileDownloadUrl"/>): it never expires and runs the
/// account's read path on every request, so handing it out here (the account list, <c>/api/me</c>) decides nothing about who
/// may see the picture. Whoever may not read the account gets the uniform 401/404, the same as for the account itself.
/// </remarks>
public static class AccountAvatar
{
    /// <summary>The <c>CustomAvatar</c> attribute's name, as the model and the download URL spell it.</summary>
    public const string Attribute = nameof(Account.CustomAvatar);

    /// <summary>The variant a list or a header draws (<c>fileOptions</c> in <c>App_Data/Model/Account.json</c>).</summary>
    public const string Variant = "thumb";

    /// <summary>The <c>Account</c> persistent object's type id, as a download URL and a custom action's parent carry it.</summary>
    public static readonly Guid AccountType = Guid.Parse(PersistentObjectIds.Default.Account);

    /// <summary>The URL to draw for <paramref name="account"/>, or null when it has neither picture.</summary>
    public static string? Resolve(Account account)
        => CustomUrl(account) ?? account.AvatarUrl;

    /// <summary>The custom avatar's download URL, or null when there is none or it is not (yet) served.</summary>
    /// <remarks>
    /// Only a clean file that has its thumbnail: a file still waiting for its scan, or refused, would answer 404, and the
    /// forge's avatar is better than a broken image.
    /// </remarks>
    public static string? CustomUrl(Account account)
        => account is { Id: { Length: > 0 } id, CustomAvatar: { ScanStatus: SparkFileScanStatus.Clean, FileId.Length: > 0 } image }
            && image.Variants.TryGetValue(Variant, out var thumb) && !string.IsNullOrEmpty(thumb?.FileId)
            ? SparkFileDownloadUrl.For(new SparkFileLocation(AccountType, id, Attribute, image, Variant))
            : null;
}
