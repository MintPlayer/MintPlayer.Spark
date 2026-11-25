using System.Security.Cryptography;
using System.Text;
using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.IdentityProvider.Models;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// Invitations to an application's team (<c>docs/identity_provider_platform_PRD.md</c> D3).
/// <para>
/// <b>No existence oracle (#453).</b> Inviting always answers the same way, and always adds the same
/// pending entry showing the typed address. The mail is sent only when the address belongs to an
/// eligible account: any account for a Tester, an active developer for an Admin or Developer. An
/// invitation that was never delivered stays Pending until it shows Expired, exactly like an ignored one.
/// </para>
/// <para>
/// The link carries a single-use token. Only its SHA-256 is stored, on the member entry, with an
/// expiry. Accepting requires being signed in as the account the address belongs to.
/// </para>
/// </summary>
internal sealed class OidcInvitations(
    ISparkStore store,
    OidcUserDocuments users,
    OidcDevelopers developers,
    SparkIdentityProviderOptions options)
{
    /// <summary>The outcome of accepting: the application's name, or why not, phrased without disclosing anything.</summary>
    public sealed record Acceptance(bool Accepted, string? ApplicationName, string? Problem);

    /// <summary>
    /// Adds a pending invitation to <paramref name="app"/> (tracked in the caller's session) and returns
    /// the token to mail, or null when no mail may go out. The caller saves, then mails.
    /// </summary>
    public async Task<string?> InviteAsync(OidcApplication app, string email, string role, string inviterId, CancellationToken ct)
    {
        var normalized = Normalize(email);

        // A new invitation to the same address replaces the pending one: one live link per address.
        foreach (var pending in app.Members.Where(m => m.Status == OidcMemberStatuses.Invited && Normalize(m.Email) == normalized))
        {
            pending.Status = OidcMemberStatuses.Revoked;
            pending.InvitationHash = null;
        }

        var token = OpaqueHandle.Generate();
        app.Members.Add(new OidcApplicationMember
        {
            MemberId = Guid.NewGuid().ToString("N"),
            Email = email.Trim(),
            Role = role,
            Status = OidcMemberStatuses.Invited,
            InvitationHash = Hash(token),
            InvitationExpiresAt = DateTime.UtcNow.Add(options.Apps.InvitationLifetime),
            InvitedBy = inviterId,
            InvitedAt = DateTime.UtcNow,
        });

        var account = await FindAccountAsync(normalized, ct);
        if (account?.Id is null || app.ActiveMember(account.Id) is not null)
            return null;

        if (role != OidcMemberRoles.Tester && !developers.IsActive(await developers.GetAsync(account.Id, ct)))
            return null;

        return token;
    }

    /// <summary>Accepts the invitation <paramref name="token"/> to <paramref name="applicationId"/> as <paramref name="userId"/>.</summary>
    public async Task<Acceptance> AcceptAsync(ISparkSession session, string applicationId, string token, string userId, CancellationToken ct)
    {
        const string invalid = "This invitation is not valid. It may have expired, been replaced, or been sent to another account.";

        var app = await session.LoadAsync<OidcApplication>(applicationId, ct);
        var hash = Hash(token);
        var entry = app?.Members.FirstOrDefault(m => m.Status == OidcMemberStatuses.Invited && m.InvitationHash is not null
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(m.InvitationHash), Encoding.ASCII.GetBytes(hash)));
        if (app is null || entry is null || entry.InvitationExpiresAt < DateTime.UtcNow)
            return new(false, null, invalid);

        // A session of its own: the request session may track the same id as the application's user type already.
        using (var lookup = store.OpenSession())
        {
            var account = (await users.LoadAsync(lookup, [userId], ct)).Values.FirstOrDefault();
            if (account?.NormalizedEmail is null || account.NormalizedEmail != Normalize(entry.Email))
                return new(false, null, invalid);
        }

        if (app.ActiveMember(userId) is not null)
            return new(false, app.DisplayName, "You are already on this application's team.");

        if (entry.Role != OidcMemberRoles.Tester && !developers.IsActive(await developers.GetAsync(userId, ct)))
            return new(false, null, "Only an approved developer can join as " + entry.Role + ". Request developer status first.");

        entry.UserId = userId;
        entry.Status = OidcMemberStatuses.Active;
        entry.AcceptedAt = DateTime.UtcNow;
        entry.InvitationHash = null;
        return new(true, app.DisplayName, null);
    }

    /// <summary>The account behind <paramref name="normalizedEmail"/>, if any, found through the user store's email reservation.</summary>
    private async Task<SparkUser?> FindAccountAsync(string normalizedEmail, CancellationToken ct)
    {
        using var session = store.OpenSession();
        return await users.FindByEmailAsync(session, normalizedEmail, ct);
    }

    /// <summary>The way ASP.NET Identity normalizes an address (<c>UpperInvariantLookupNormalizer</c>).</summary>
    private static string Normalize(string? email) => (email ?? "").Trim().ToUpperInvariant();

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
