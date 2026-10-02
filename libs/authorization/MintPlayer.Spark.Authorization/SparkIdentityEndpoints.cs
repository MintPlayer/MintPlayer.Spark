using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using MintPlayer.AspNetCore.Endpoints;

namespace MintPlayer.Spark.Authorization;

/// <summary>
/// Stand-in endpoint types for the account endpoints that have no endpoint class: the part of
/// <c>MapIdentityApi</c> Spark keeps, and Spark's own replacements for the rest. Each mapped route
/// carries an <see cref="EndpointTypeMetadata"/> naming one of these, so any code can ask whether it
/// is served with <see cref="EndpointDataSourceExtensions.IsEndpointMapped{TEndpoint}"/> instead of
/// matching route strings:
/// <code>
/// endpointDataSource.IsEndpointMapped&lt;SparkIdentityEndpoints.TwoFactor&gt;()
/// </code>
/// </summary>
/// <remarks>
/// Ask at request time or from <c>ApplicationStarted</c> on: the container's
/// <see cref="EndpointDataSource"/> is empty until the host has started. Which of these are mapped
/// depends on <see cref="Configuration.SparkLocalCredentials"/>; an endpoint the mode excludes is
/// absent, so it answers <see langword="false"/>.
/// </remarks>
public static class SparkIdentityEndpoints
{
    /// <summary><c>POST /spark/auth/login</c> (Microsoft's): password sign-in.</summary>
    public sealed class Login { private Login() { } }

    /// <summary><c>POST /spark/auth/refresh</c> (Microsoft's): bearer-token refresh.</summary>
    public sealed class Refresh { private Refresh() { } }

    /// <summary><c>POST /spark/auth/manage/2fa</c> (Microsoft's): read and change two-factor settings.</summary>
    public sealed class TwoFactor { private TwoFactor() { } }

    /// <summary><c>GET /spark/auth/manage/2fa/authenticator-uri</c>: the authenticator QR payload.</summary>
    public sealed class AuthenticatorUri { private AuthenticatorUri() { } }

    /// <summary><c>GET /spark/auth/manage/info</c> (Microsoft's): the signed-in user's email.</summary>
    public sealed class Info { private Info() { } }

    /// <summary><c>POST /spark/auth/manage/info</c>: change email or password.</summary>
    public sealed class UpdateInfo { private UpdateInfo() { } }

    /// <summary><c>POST /spark/auth/manage/password</c>: set a password on an account without one.</summary>
    public sealed class SetPassword { private SetPassword() { } }

    /// <summary><c>POST /spark/auth/register</c>: self-registration.</summary>
    public sealed class Register { private Register() { } }

    /// <summary><c>POST /spark/auth/resendConfirmationEmail</c>.</summary>
    public sealed class ResendConfirmationEmail { private ResendConfirmationEmail() { } }

    /// <summary><c>POST /spark/auth/forgotPassword</c>.</summary>
    public sealed class ForgotPassword { private ForgotPassword() { } }

    /// <summary><c>POST /spark/auth/resetPassword</c>.</summary>
    public sealed class ResetPassword { private ResetPassword() { } }

    /// <summary><c>GET /spark/auth/confirmEmail</c> and <c>POST /spark/auth/confirm-email</c>.</summary>
    public sealed class ConfirmEmail { private ConfirmEmail() { } }

    /// <summary><c>GET /spark/auth/manage/profile</c>.</summary>
    public sealed class Profile { private Profile() { } }

    /// <summary><c>POST /spark/auth/manage/profile</c>.</summary>
    public sealed class UpdateProfile { private UpdateProfile() { } }

    /// <summary><c>GET /spark/auth/manage/personal-data</c>.</summary>
    public sealed class PersonalData { private PersonalData() { } }

    /// <summary><c>DELETE /spark/auth/manage/account</c>.</summary>
    public sealed class DeleteAccount { private DeleteAccount() { } }

    /// <summary>Records <typeparamref name="TEndpoint"/> as the endpoint type of a lambda route.</summary>
    internal static RouteHandlerBuilder Is<TEndpoint>(this RouteHandlerBuilder builder)
        => builder.WithMetadata(new EndpointTypeMetadata(typeof(TEndpoint)));
}
