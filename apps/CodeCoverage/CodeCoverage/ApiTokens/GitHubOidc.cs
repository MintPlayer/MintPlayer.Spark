using CodeCoverage.Forge;

namespace CodeCoverage.ApiTokens;

/// <summary>
/// GitHub Actions OIDC constants, now a thin projection of
/// <see cref="ForgeOidcProfile.GitHub"/>.
/// </summary>
/// <remarks>
/// Kept as a named surface because the scheme name is referenced from an attribute
/// (<c>[Authorize(AuthenticationSchemes = …)]</c>), which needs a compile-time constant and cannot
/// read a record property. Everything else defers to the profile so the two cannot drift — before
/// this, the claim names existed only here and a second forge would have had to copy the file.
/// <para>
/// ⚠️ New code should read <see cref="ForgeOidcProfile"/>. This exists for the attribute and for
/// the startup registration, and goes away when the GitHub library owns both (M15).
/// </para>
/// </remarks>
public static class GitHubOidc
{
    /// <summary>Must be a constant: it is used in an attribute argument.</summary>
    public const string SchemeName = "GitHubOidc";

    /// <summary>The profile every claim name comes from.</summary>
    public static ForgeOidcProfile Profile => ForgeOidcProfile.GitHub;

    public static string Issuer => Profile.Issuer;
}
