using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Authorization.Configuration;

namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>
/// Builds the links Spark puts in account mail (#460, D16): to the SPA's pages, whose components post
/// the token back to the API — not to the server's plain-text <c>confirmEmail</c>.
/// </summary>
/// <remarks>
/// Replace the registration to change the shape entirely; to move a page, set
/// <see cref="SparkAuthLinkOptions"/>. Tokens arrive already base64url-encoded.
/// </remarks>
public interface ISparkAuthLinkBuilder
{
    /// <summary>The link that confirms <paramref name="userId"/>'s email — or, with <paramref name="changedEmail"/>, the change to it.</summary>
    /// <exception cref="InvalidOperationException">No public base URL is configured outside Development.</exception>
    string ConfirmEmail(HttpContext context, string userId, string code, string? changedEmail = null);

    /// <summary>The link that completes a password reset for <paramref name="email"/>.</summary>
    /// <exception cref="InvalidOperationException">No public base URL is configured outside Development.</exception>
    string ResetPassword(HttpContext context, string email, string code);
}

/// <summary>
/// The default <see cref="ISparkAuthLinkBuilder"/>: <see cref="SparkAuthLinkOptions.PublicBaseUrl"/>
/// (or <c>Spark:Auth:PublicBaseUrl</c>) + the configured page path + the query.
/// </summary>
/// <remarks>
/// ⚠️ Outside Development an unset base URL throws rather than falling back to the request's
/// <c>Host</c>: on an anonymous endpoint such as <c>forgotPassword</c> that header is the attacker's to
/// choose, and a reset token mailed in a link to the attacker's host is a takeover (password-reset
/// poisoning). Callers log the refusal and send nothing.
/// </remarks>
public sealed class SparkAuthLinkBuilder : ISparkAuthLinkBuilder
{
    private readonly SparkAuthLinkOptions links;
    private readonly IConfiguration? configuration;
    private readonly IHostEnvironment? environment;

    public SparkAuthLinkBuilder(IOptions<SparkAuthenticationOptions> options, IConfiguration? configuration = null, IHostEnvironment? environment = null)
    {
        links = options.Value.Links;
        this.configuration = configuration;
        this.environment = environment;
    }

    public string ConfirmEmail(HttpContext context, string userId, string code, string? changedEmail = null)
    {
        var query = new Dictionary<string, string?> { ["userId"] = userId, ["code"] = code };
        if (!string.IsNullOrEmpty(changedEmail))
            query["changedEmail"] = changedEmail;

        return QueryHelpers.AddQueryString(BaseUrl(context) + NormalizePath(links.ConfirmEmailPath), query);
    }

    public string ResetPassword(HttpContext context, string email, string code)
        => QueryHelpers.AddQueryString(
            BaseUrl(context) + NormalizePath(links.ResetPasswordPath),
            new Dictionary<string, string?> { ["email"] = email, ["code"] = code });

    private string BaseUrl(HttpContext context)
    {
        var configured = links.PublicBaseUrl ?? configuration?["Spark:Auth:PublicBaseUrl"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.TrimEnd('/');

        if (environment?.IsDevelopment() == true)
            return $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}";

        throw new InvalidOperationException(
            "Spark cannot build an account-mail link: no public base URL is configured. Set "
            + "'Spark:Auth:PublicBaseUrl' (e.g. https://example.com) or SparkAuthenticationOptions.Links.PublicBaseUrl. "
            + "Outside Development the request's Host header is not used, because on an anonymous endpoint it is "
            + "attacker-chosen (password-reset poisoning).");
    }

    private static string NormalizePath(string path) => path.StartsWith('/') ? path : "/" + path;
}
