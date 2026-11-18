using System.Security.Cryptography;
using System.Text;

namespace MintPlayer.Spark.Apps;

/// <summary>
/// Who owns webhooks and receives events: a user, an organization, or any kind a site adds through
/// <see cref="IWebhookOwnerProvider"/> (D18). <see cref="Key"/> is the owner's id within its kind.
/// </summary>
public readonly record struct WebhookOwner(string Kind, string Key)
{
    public const string UserKind = "user";
    public const string OrganizationKind = "organization";

    public static WebhookOwner User(string userId) => new(UserKind, userId);

    public static WebhookOwner Organization(string organizationId) => new(OrganizationKind, organizationId);

    /// <summary>
    /// A fixed-length, id-safe token for document ids (an owner key such as <c>SparkUsers/1-A</c> holds slashes).
    /// </summary>
    internal string IdToken
    {
        get
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Kind + "\n" + Key));
            return Convert.ToHexStringLower(hash, 0, 16);
        }
    }

    public override string ToString() => $"{Kind}:{Key}";
}
