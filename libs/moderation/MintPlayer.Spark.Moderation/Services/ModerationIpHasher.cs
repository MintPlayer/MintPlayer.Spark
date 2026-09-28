using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Moderation.Documents;
using Raven.Client.Documents;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Moderation.Services;

/// <summary>
/// Fraud measure 9 (GDPR): what is stored about a network is an HMAC-SHA256 of the <b>truncated</b>
/// address — the /24 of an IPv4 address, the /48 of an IPv6 one — under a key that rotates every
/// <c>IpKeyRotationDays</c>. The address itself is never stored.
/// </summary>
/// <remarks>
/// <para>
/// The key material is kept in the database <b>protected by Data Protection</b>, whose key ring
/// lives outside the database (<c>Spark:DataProtection:KeysPath</c>): a database dump alone holds
/// hashes and ciphertext, not a key to brute-force the 2^24 prefixes with. A key document expires
/// with the last observation made under it (rotation + retention), after which old hashes can no
/// longer be linked to anything.
/// </para>
/// <para>
/// Two observations only match under the same key period, so "shares a network" means "within the
/// same rotation period" — enough for the registration-cluster rule, which looks at accounts
/// created within an hour of each other.
/// </para>
/// </remarks>
internal sealed partial class ModerationIpHasher
{
    private const string Purpose = "MintPlayer.Spark.Moderation.IpHashKey.v1";
    private static readonly ConcurrentDictionary<string, byte[]> Keys = new();

    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IDataProtectionProvider dataProtection;
    [Inject] private readonly IOptions<SparkModerationOptions> options;
    [Inject] private readonly TimeProvider timeProvider;

    /// <summary>The truncated network of <paramref name="address"/>: /24 (IPv4, incl. IPv4-mapped IPv6) or /48 (IPv6).</summary>
    public static byte[] Truncate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork ? bytes[..3] : bytes[..6];
    }

    /// <summary>The key id and hash for <paramref name="address"/> at the current instant.</summary>
    public async Task<(string KeyId, string Hash)> HashAsync(IPAddress address, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var fraud = options.Value.Fraud;
        var period = (long)Math.Floor((now - DateTime.UnixEpoch).TotalDays / fraud.IpKeyRotationDays);
        var keyId = ModerationIds.IpKey(period);
        var key = await GetKeyAsync(keyId, now, fraud, cancellationToken);
        var network = Truncate(address);
        var hash = Convert.ToHexString(HMACSHA256.HashData(key, network)).ToLowerInvariant();
        return (keyId, hash[..32]);
    }

    private async Task<byte[]> GetKeyAsync(string keyId, DateTime now, ModerationFraudOptions fraud, CancellationToken cancellationToken)
    {
        var cacheKey = documentStore.Database + "|" + keyId;
        if (Keys.TryGetValue(cacheKey, out var cached))
            return cached;

        var protector = dataProtection.CreateProtector(Purpose);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var session = documentStore.OpenAsyncSession();
            var stored = await session.LoadAsync<ModerationIpKey>(keyId, cancellationToken);
            if (stored is not null)
                return Keys[cacheKey] = protector.Unprotect(Convert.FromBase64String(stored.ProtectedKey));

            var fresh = RandomNumberGenerator.GetBytes(32);
            var document = new ModerationIpKey { ProtectedKey = Convert.ToBase64String(protector.Protect(fresh)), CreatedAtUtc = now };
            await session.StoreAsync(document, string.Empty, keyId, cancellationToken);
            session.Advanced.GetMetadataFor(document)["@expires"] = now.AddDays(fraud.IpKeyRotationDays + fraud.IpObservationRetentionDays).ToString("O");
            try
            {
                await session.SaveChangesAsync(cancellationToken);
                return Keys[cacheKey] = fresh;
            }
            catch (ConcurrencyException)
            {
                // Another node created this period's key first: read theirs.
            }
        }
        throw new InvalidOperationException($"Could not create or read the moderation network key {keyId}.");
    }
}
