using System.Text.Json;
using System.Text.Json.Serialization;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Extensions;

// The request and response contracts of Spark's account endpoints (Endpoints/Account). They keep this
// namespace, where they were declared next to the handlers they now outlive, because they are public.

/// <summary>
/// Body of <c>POST /spark/auth/register</c>: Microsoft's <c>RegisterRequest</c> plus the required
/// <see cref="UserName"/>, the public handle other users see (G-Q22). It may not contain <c>@</c>.
/// </summary>
public sealed class SparkRegisterRequest
{
    public string? Email { get; set; }
    public string? Password { get; set; }
    public string? UserName { get; set; }
}

/// <summary>Body of <c>POST /spark/auth/confirm-email</c>: the query of the mailed link.</summary>
public sealed class SparkConfirmEmailRequest
{
    public string? UserId { get; set; }
    public string? Code { get; set; }
    /// <summary>Set when the link confirms an email <em>change</em>.</summary>
    public string? ChangedEmail { get; set; }
}

/// <summary>Body of <c>POST /spark/auth/manage/password</c>. <see cref="CurrentPassword"/> is required when the account already has one.</summary>
public sealed class SparkSetPasswordRequest
{
    public string? CurrentPassword { get; set; }
    public string? NewPassword { get; set; }
}

/// <summary>Body of <c>POST /spark/auth/manage/profile</c>.</summary>
public sealed class SparkProfileRequest
{
    /// <summary>A new user name, or <see langword="null"/> to keep it.</summary>
    public string? UserName { get; set; }
    /// <summary>Application fields, each owned by an <see cref="ISparkProfileContributor{TUser}"/>.</summary>
    public Dictionary<string, JsonElement>? Fields { get; set; }

    private string? preferredCulture;

    /// <summary>
    /// The culture the user's mail is written in (<see cref="SparkUser.PreferredCulture"/>): absent keeps
    /// it, <see langword="null"/> or empty clears it, otherwise a predefined culture name (<c>nl-BE</c>);
    /// anything else is a validation problem under <c>PreferredCulture</c>.
    /// </summary>
    public string? PreferredCulture
    {
        get => preferredCulture;
        set { preferredCulture = value; HasPreferredCulture = true; }
    }

    /// <summary>Whether the request carried <see cref="PreferredCulture"/> at all (an explicit null counts).</summary>
    [JsonIgnore]
    public bool HasPreferredCulture { get; private set; }
}

/// <summary>Answer of <c>GET/POST /spark/auth/manage/profile</c>.</summary>
public sealed record SparkProfileResponse(string? UserName, string? Email, IReadOnlyDictionary<string, object?> Fields)
{
    /// <summary><see cref="SparkUser.PreferredCulture"/>; <see langword="null"/> when unset (the mail default applies).</summary>
    public string? PreferredCulture { get; init; }
}

/// <summary>Answer of <c>GET /spark/auth/manage/2fa/authenticator-uri</c>.</summary>
public sealed record SparkAuthenticatorUriResponse(string SharedKey, string AuthenticatorUri, string QrCodeSvg);

/// <summary>Optional body of <c>DELETE /spark/auth/manage/account</c>.</summary>
public sealed class SparkDeleteAccountRequest
{
    /// <summary>The current password. Without it, the sign-in must be recent.</summary>
    public string? Password { get; set; }
}
