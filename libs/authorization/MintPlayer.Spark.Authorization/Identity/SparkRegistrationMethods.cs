namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>The values of <see cref="SparkUser.RegistrationMethod"/>.</summary>
public static class SparkRegistrationMethods
{
    /// <summary>Self-service registration with a password (<c>/spark/auth/register</c>).</summary>
    public const string Password = "password";

    /// <summary>Created by anything else — typically an administrator or a seed, with no credential attached yet.</summary>
    public const string Other = "other";

    /// <summary>The prefix of an external-provider registration: <c>external:{scheme}</c>.</summary>
    public const string ExternalPrefix = "external:";

    /// <summary>Provisioned by the external-login callback for <paramref name="provider"/> (the authentication scheme).</summary>
    public static string External(string provider) => ExternalPrefix + provider;
}
