using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Embed;

namespace MintPlayer.Spark.Embed;

/// <summary>
/// The top-level hand-over of embed mode (commerce PRD X23, GB8). Inside an embed on the organiser's site, the platform's
/// cookies (the X1 visitor cookie, the session, XSRF) are third-party and may be blocked, so nothing that needs them
/// (a hold, a sign-in, a waiting room) happens in the frame: the embed asks for a hand-over and opens it in a popup or
/// the top window. The state travels in a signed, short-lived token in the URL, not in a cookie.
/// </summary>
/// <remarks>
/// The token proves the platform issued it, which is what makes the <c>ReturnUrl</c> inside it safe to follow: it was
/// checked against the allowed embedding origins when issued (no open redirect). It is a bearer token for the payload
/// only; it grants no authority (the payload is the visitor's own selection, revalidated by whatever acts on it).
/// </remarks>
internal sealed class SparkEmbedHandoverService(
    IOptions<SparkEmbedOptions> options,
    IDataProtectionProvider dataProtection,
    IHttpContextAccessor accessor,
    IServiceProvider services)
{
    internal const string ProtectorPurpose = "MintPlayer.Spark.Embed.Handover.v1";
    internal const string QueryParameter = "spark-handover";
    internal const int MaxPayloadBytes = 2048;

    private sealed record Envelope(string P, string? D, string? R);

    private TimeProvider Time => services.GetService<TimeProvider>() ?? TimeProvider.System;

    private ITimeLimitedDataProtector Protector()
        => dataProtection.CreateProtector(ProtectorPurpose).ToTimeLimitedDataProtector();

    /// <summary>The hand-over URL, or <see langword="null"/> with <paramref name="error"/> when the request is refused.</summary>
    internal SparkEmbedHandover? Create(SparkEmbedHandoverRequest request, out string? error)
    {
        var o = options.Value;
        error = null;
        if (!o.Enabled)
        {
            error = "Embed mode is off.";
            return null;
        }
        if (!IsPlatformPath(request.Path))
        {
            error = "Path must be a path on this site.";
            return null;
        }
        if (request.ReturnUrl is { } returnUrl && !IsAllowedReturnUrl(returnUrl, o))
        {
            error = "ReturnUrl must be on an allowed embedding origin.";
            return null;
        }
        var payload = request.Payload is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } element ? element.GetRawText() : null;
        if (payload is not null && System.Text.Encoding.UTF8.GetByteCount(payload) > MaxPayloadBytes)
        {
            error = $"Payload exceeds {MaxPayloadBytes} bytes.";
            return null;
        }

        var origin = SparkPublicBaseUrl.Resolve(o.PublicOrigin, services.GetService<IConfiguration>(), services.GetService<IHostEnvironment>(), accessor.HttpContext);
        if (origin is null)
        {
            error = "No public origin: set Spark:Embed:PublicOrigin or Spark:Auth:PublicBaseUrl.";
            return null;
        }

        var expires = Time.GetUtcNow() + o.HandoverLifetime;
        var token = Protector().Protect(JsonSerializer.Serialize(new Envelope(request.Path, payload, request.ReturnUrl)), expires);
        var separator = request.Path.Contains('?') ? '&' : '?';
        return new SparkEmbedHandover(origin + request.Path + separator + QueryParameter + "=" + Uri.EscapeDataString(token), expires);
    }

    /// <summary>The state of a token, or <see langword="null"/> when it is forged, tampered or expired.</summary>
    internal SparkEmbedHandoverState? Redeem(string? token)
    {
        if (!options.Value.Enabled || string.IsNullOrWhiteSpace(token))
            return null;
        try
        {
            var json = Protector().Unprotect(token, out var expiration);
            if (expiration < Time.GetUtcNow())
                return null;
            var envelope = JsonSerializer.Deserialize<Envelope>(json);
            if (envelope is null)
                return null;
            JsonElement? payload = envelope.D is null ? null : JsonDocument.Parse(envelope.D).RootElement.Clone();
            return new SparkEmbedHandoverState(envelope.P, payload, envelope.R);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
        {
            return null;
        }
    }

    /// <summary>A rooted path on this site: not protocol-relative, no backslash (browsers read <c>/\host</c> as <c>//host</c>), no scheme.</summary>
    internal static bool IsPlatformPath(string? path)
        => path is { Length: > 0 and <= 512 }
           && path[0] == '/'
           && !path.StartsWith("//", StringComparison.Ordinal)
           && !path.Contains('\\')
           && !path.Any(char.IsControl);

    internal static bool IsAllowedReturnUrl(string returnUrl, SparkEmbedOptions options)
        => Uri.TryCreate(returnUrl, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
           && SparkEmbed.IsAllowedOrigin(uri.GetLeftPart(UriPartial.Authority), options.AllowedOrigins);
}
