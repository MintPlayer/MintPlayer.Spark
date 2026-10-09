using System.Security.Cryptography;
using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// The device authorization grant's records (RFC 8628, <c>docs/identity_provider_platform_PRD.md</c> D8).
/// A device authorization is an <see cref="OidcToken"/> of type <see cref="OidcTokenTypes.DeviceCode"/>,
/// keyed by the hash of its device code, with the user code in <c>Properties</c>; a second, tiny
/// document keyed by the hash of the user code points at it, so the code a person types is a point-load too.
/// </summary>
internal static class OidcDeviceCodes
{
    public const string GrantType = "urn:ietf:params:oauth:grant-type:device_code";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public const int IntervalSeconds = 5;

    /// <summary>RFC 8628 §6.1: no vowels (no words), no lookalikes; 8 characters is ~34 bits against a short-lived, rate-limited form.</summary>
    private const string UserCodeAlphabet = "BCDFGHJKLMNPQRSTVWXZ";

    public static string DeviceDocumentId(string deviceCode) => OidcTokenReference.DocumentId("device:" + deviceCode);

    public static string UserCodeDocumentId(string userCode) => OidcTokenReference.DocumentId("usercode:" + Normalize(userCode));

    /// <summary>The user code as typed, upper-cased, without the dash or spaces.</summary>
    public static string Normalize(string userCode) => new(userCode.ToUpperInvariant().Where(char.IsLetter).ToArray());

    public static string NewUserCode()
    {
        Span<char> chars = stackalloc char[8];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = UserCodeAlphabet[RandomNumberGenerator.GetInt32(UserCodeAlphabet.Length)];
        return $"{chars[..4]}-{chars[4..]}";
    }

    /// <summary>The device authorization behind a typed user code, if it is still waiting.</summary>
    public static async Task<OidcToken?> FindByUserCodeAsync(IAsyncDocumentSession session, string userCode, CancellationToken ct)
    {
        var pointer = await session.LoadAsync<OidcToken>(UserCodeDocumentId(userCode), ct);
        if (pointer?.Properties.GetValueOrDefault("device") is not { } deviceId)
            return null;
        var device = await session.LoadAsync<OidcToken>(deviceId, ct);
        return device is { Type: OidcTokenTypes.DeviceCode, Status: "pending" } && device.ExpiresAt > DateTime.UtcNow ? device : null;
    }
}
