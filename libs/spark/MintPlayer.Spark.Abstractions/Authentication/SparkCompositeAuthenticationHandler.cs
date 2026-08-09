using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Abstractions.Authentication;

/// <summary>
/// Well-known names for Spark's own authentication plumbing.
/// </summary>
public static class SparkAuthenticationDefaults
{
    /// <summary>
    /// The scheme Spark installs as <c>DefaultAuthenticateScheme</c>. It owns no credential of its
    /// own — it delegates to each registered credential scheme in turn.
    /// </summary>
    public const string CompositeScheme = "Spark:Composite";
}

/// <summary>
/// Tries each registered credential scheme in order and adopts the first that succeeds.
/// <para>
/// This exists because Spark's endpoints carry no authorization metadata: they are anonymous to
/// ASP.NET and authorize inside the handler. ASP.NET runs only the <i>default authenticate
/// scheme</i> unless an endpoint names another, so before this handler an app could register a
/// certificate or bearer scheme and have it never execute on a Spark endpoint at all — the caller
/// simply arrived anonymous and got <c>Everyone</c> rights (F7). Making one scheme the default and
/// having it fan out is what turns credential registration into something that takes effect.
/// </para>
/// <para>
/// A composite rather than <c>AddPolicyScheme</c> + <c>ForwardDefaultSelector</c>: a policy scheme
/// sniffs the request and picks exactly one handler, which means encoding "what does a certificate
/// request look like?" up front. Handlers already answer that question correctly by returning
/// <see cref="AuthenticateResult.NoResult"/>, so asking each in turn needs no such guesswork.
/// </para>
/// </summary>
internal sealed class SparkCompositeAuthenticationHandler
    : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly SparkModuleRegistry registry;

    public SparkCompositeAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        SparkModuleRegistry registry)
        : base(options, logger, encoder)
    {
        this.registry = registry;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        List<Exception>? rejections = null;

        foreach (var credential in registry.CredentialSchemes)
        {
            // Guard against a caller declaring the composite as one of its own members, which
            // would recurse until the stack ran out.
            if (string.Equals(credential.Name, SparkAuthenticationDefaults.CompositeScheme, StringComparison.Ordinal))
                continue;

            var result = await Context.AuthenticateAsync(credential.Name);

            if (result.Succeeded)
            {
                // Recorded for the antiforgery gate, which must decide from what actually
                // authenticated the request rather than from which cookies or headers happen to be
                // present — otherwise the gate could be suppressed by request shape.
                Context.Features.Set<ISparkAuthenticatedSchemeFeature>(
                    new SparkAuthenticatedSchemeFeature(credential));

                return result;
            }

            if (result.Failure is not null)
                (rejections ??= []).Add(result.Failure);
        }

        if (rejections is null)
            return AuthenticateResult.NoResult();

        // Something was presented and every scheme refused it. Report the failure rather than
        // reporting anonymity: the two are indistinguishable downstream (both yield Everyone-only
        // rights), and treating a rejected credential as "no credential" is precisely the silence
        // F7 called out.
        var failure = rejections.Count == 1
            ? rejections[0]
            : new AggregateException("No registered credential scheme accepted the presented credential.", rejections);

        Logger.LogWarning(failure,
            "A credential was presented and refused by every registered scheme ({Schemes}). The request continues as anonymous.",
            string.Join(", ", registry.CredentialSchemes.Select(s => s.Name)));

        return AuthenticateResult.Fail(failure);
    }
}

/// <summary>
/// Records which credential scheme authenticated the current request. Set by
/// <see cref="SparkCompositeAuthenticationHandler"/> on success.
/// </summary>
public interface ISparkAuthenticatedSchemeFeature
{
    /// <summary>The scheme that produced <c>HttpContext.User</c>.</summary>
    SparkCredentialScheme Scheme { get; }
}

internal sealed class SparkAuthenticatedSchemeFeature(SparkCredentialScheme scheme)
    : ISparkAuthenticatedSchemeFeature
{
    public SparkCredentialScheme Scheme { get; } = scheme;
}
