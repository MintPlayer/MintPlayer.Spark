using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Configuration;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Reads, issues and clears the visitor cookies behind
/// <see cref="Abstractions.Authentication.ISparkCurrentPrincipal.VisitorId"/> (<c>docs/notification_bell_PRD.md</c>
/// R1). The id is 128 random bits, protected with a time-limited Data Protection protector, so a cookie
/// that was tampered with, issued by another application or kept past <see cref="SparkVisitorOptions.Lifetime"/>
/// reads as "no visitor".
/// </summary>
[Register(ServiceLifetime.Singleton)]
internal sealed partial class SparkVisitorCookies
{
    /// <summary>The Data Protection purpose: a payload protected for another purpose never unprotects here.</summary>
    internal const string Purpose = "MintPlayer.Spark.Visitor";

    /// <summary>Where the id read or issued during this request is kept, so a fresh id is seen at once.</summary>
    private static readonly object ItemKey = new();

    [Inject] private readonly IDataProtectionProvider dataProtection;
    [Inject] private readonly IOptions<SparkVisitorOptions> options;

    private ITimeLimitedDataProtector? protector;
    private ITimeLimitedDataProtector Protector => protector ??= dataProtection.CreateProtector(Purpose).ToTimeLimitedDataProtector();

    /// <summary>The visitor id (<c>visitor:…</c>) of this request, or <see langword="null"/>.</summary>
    public string? Read(HttpContext context)
    {
        if (context.Items.TryGetValue(ItemKey, out var cached))
            return cached as string;

        var id = Unprotect(context.Request.Cookies[SparkVisitorOptions.CookieName]);
        context.Items[ItemKey] = id;
        return id;
    }

    /// <summary>
    /// Issues the cookies for a new visitor, or renews them for the current one (sliding lifetime), and
    /// returns the id. <see langword="null"/> when the response has started and there is no visitor yet: a
    /// cookie can no longer be set, and an id the browser never learns would address nobody.
    /// </summary>
    public string? Ensure(HttpContext context)
    {
        var current = Read(context);
        if (context.Response.HasStarted)
            return current;

        var raw = current?[SparkVisitorOptions.IdPrefix.Length..] ?? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var settings = options.Value;
        var protectedValue = Protector.Protect(raw, settings.Lifetime);

        context.Response.Cookies.Append(SparkVisitorOptions.CookieName, protectedValue, CookieOptions(context, httpOnly: true));
        context.Response.Cookies.Append(SparkVisitorOptions.PresenceCookieName, "1", CookieOptions(context, httpOnly: false));

        var id = SparkVisitorOptions.IdPrefix + raw;
        context.Items[ItemKey] = id;
        return id;
    }

    /// <summary>Clears both cookies (after a sign-in claimed the visitor).</summary>
    public void Clear(HttpContext context)
    {
        context.Items[ItemKey] = null;
        if (context.Response.HasStarted)
            return;
        var expired = new CookieOptions { Path = "/", Secure = context.Request.IsHttps, SameSite = SameSiteMode.Lax };
        context.Response.Cookies.Delete(SparkVisitorOptions.CookieName, expired);
        context.Response.Cookies.Delete(SparkVisitorOptions.PresenceCookieName, expired);
    }

    /// <summary>The protected payload's id, or <see langword="null"/> for a missing, foreign, tampered or expired one.</summary>
    internal string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        try
        {
            var raw = Protector.Unprotect(value, out _);
            return raw.Length == 32 && raw.All(Uri.IsHexDigit) ? SparkVisitorOptions.IdPrefix + raw : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private CookieOptions CookieOptions(HttpContext context, bool httpOnly)
    {
        var settings = options.Value;
        return new CookieOptions
        {
            HttpOnly = httpOnly,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            IsEssential = true,
            Expires = settings.SessionCookie ? null : DateTimeOffset.UtcNow + settings.Lifetime,
        };
    }
}
