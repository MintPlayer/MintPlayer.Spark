using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;

namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>How a change to the signed-in user's passkeys ended.</summary>
internal enum SparkPasskeyOutcome
{
    Done,

    /// <summary>Passkeys are disabled, or nobody is signed in: the page does not exist for this request.</summary>
    Unavailable,

    /// <summary>The credential is not one of <em>this</em> user's, whoever else may hold it.</summary>
    NotFound,

    /// <summary>The ceremony or the store refused, for a reason the caller is not told.</summary>
    Refused,

    /// <summary>⚠️ It is the account's last way in; removing it would lock the owner out for good.</summary>
    LastCredential,

    /// <summary>
    /// The enrollment ceremony's state is gone: expired (5 minutes), already used, or never started
    /// in this browser. Starting again is the only remedy.
    /// </summary>
    Expired,
}

/// <summary>
/// The signed-in user's passkeys, for the generic passkeys page (<c>PasskeysActions</c>,
/// <c>PasskeyRowActions</c> and the three custom actions).
/// </summary>
/// <remarks>
/// <para>
/// Non-generic because those classes are resolved by name, and a name cannot carry the
/// application's <c>TUser</c>. <see cref="SparkPasskeyAccount{TUser}"/> closes it at registration,
/// where <c>AddSparkAuthentication&lt;TUser&gt;</c> already knows it — the same reason the endpoints
/// are mapped by <c>MapSparkIdentityApi&lt;TUser&gt;</c>.
/// </para>
/// <para>
/// ⚠️ <b>Every member starts from the request principal and takes no user.</b> That is the whole
/// isolation story of the page: a credential id from somebody else's account is simply not found
/// among this user's, so neither the query nor an action can reach another user's passkeys however
/// the request is shaped (PRD D2, D4).
/// </para>
/// </remarks>
internal interface ISparkPasskeyAccount
{
    /// <summary>Whether passkeys are enabled and the request's principal resolves to a user.</summary>
    Task<bool> IsAvailableAsync();

    /// <summary>The user's passkeys, metadata only; empty when <see cref="IsAvailableAsync"/> is false.</summary>
    Task<IReadOnlyList<UserPasskeyInfo>> ListAsync();

    /// <summary>One of the user's passkeys by its base64url credential id, or null.</summary>
    Task<UserPasskeyInfo?> FindAsync(string credentialId);

    /// <summary>
    /// Starts an enrollment ceremony: the WebAuthn creation options, and the DataProtection-protected
    /// state cookie on the response. Null when unavailable.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Not idempotent.</b> Each call issues a new challenge and overwrites the cookie, so the
    /// attestation of an earlier one fails. Call it once per ceremony (PRD D7).
    /// </remarks>
    Task<JsonElement?> CreationOptionsAsync();

    /// <summary>Completes the ceremony started by <see cref="CreationOptionsAsync"/> with the browser's credential.</summary>
    Task<SparkPasskeyOutcome> RegisterAsync(string credentialJson);

    /// <summary>Renames one of the user's passkeys; the name is sanitized (64 characters, no control characters).</summary>
    Task<SparkPasskeyOutcome> RenameAsync(string credentialId, string? name);

    /// <summary>Removes one of the user's passkeys, unless it is the last thing that can sign the account in.</summary>
    Task<SparkPasskeyOutcome> RemoveAsync(string credentialId);
}

/// <inheritdoc cref="ISparkPasskeyAccount"/>
internal sealed partial class SparkPasskeyAccount<TUser> : ISparkPasskeyAccount
    where TUser : SparkUser, new()
{
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly UserManager<TUser> userManager;
    [Inject] private readonly SignInManager<TUser> signInManager;
    [Inject] private readonly IOptions<SparkAuthenticationOptions> options;

    private TUser? user;
    private bool resolved;

    /// <summary>
    /// The signed-in user, resolved once per scope. Null when passkeys are disabled, when nobody is
    /// signed in, and when an authenticated cookie names a user that no longer exists.
    /// </summary>
    private async Task<TUser?> UserAsync()
    {
        if (resolved)
            return user;

        resolved = true;
        if (options.Value.Passkeys != SparkPasskeys.Enabled)
            return null;

        var principal = httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
            return null;

        return user = await userManager.GetUserAsync(principal);
    }

    public async Task<bool> IsAvailableAsync() => await UserAsync() is not null;

    public async Task<IReadOnlyList<UserPasskeyInfo>> ListAsync()
        => await UserAsync() is { } current ? [.. await userManager.GetPasskeysAsync(current)] : [];

    public async Task<UserPasskeyInfo?> FindAsync(string credentialId)
    {
        if (await UserAsync() is not { } current || !PasskeyEndpoints.TryDecodeCredentialId(credentialId, out var id))
            return null;

        return await userManager.GetPasskeyAsync(current, id);
    }

    public async Task<JsonElement?> CreationOptionsAsync()
    {
        if (await UserAsync() is not { } current)
            return null;

        var entity = new PasskeyUserEntity
        {
            Id = await userManager.GetUserIdAsync(current),
            Name = await userManager.GetUserNameAsync(current) ?? string.Empty,
            // The account name rather than the email, as the creation-options endpoint does: the
            // authenticator shows and stores this string, so it should be recognisable without being
            // more identifying than the account already is.
            DisplayName = await userManager.GetUserNameAsync(current) ?? string.Empty,
        };

        var optionsJson = await signInManager.MakePasskeyCreationOptionsAsync(entity);
        using var document = JsonDocument.Parse(optionsJson);
        return document.RootElement.Clone();
    }

    public async Task<SparkPasskeyOutcome> RegisterAsync(string credentialJson)
    {
        if (await UserAsync() is not { } current)
            return SparkPasskeyOutcome.Unavailable;

        if (string.IsNullOrWhiteSpace(credentialJson))
            return SparkPasskeyOutcome.Refused;

        PasskeyAttestationResult attestation;
        try
        {
            attestation = await signInManager.PerformPasskeyAttestationAsync(credentialJson);
        }
        catch (InvalidOperationException)
        {
            // "No passkey attestation is underway": the ceremony's state cookie is missing, already
            // used (retrieving it signs it out), or past its 5 minutes (Identity's TwoFactorUserId
            // cookie, ExpireTimeSpan; the browser's own timeout is IdentityPasskeyOptions'
            // AuthenticatorTimeout, also 5 minutes). Told apart from a refusal only so the user hears
            // "try again", which is all this says.
            return SparkPasskeyOutcome.Expired;
        }
        catch (Exception ex) when (PasskeyEndpoints.IsCeremonyInputFailure(ex))
        {
            return SparkPasskeyOutcome.Refused;
        }

        if (!attestation.Succeeded)
            return SparkPasskeyOutcome.Refused;

        try
        {
            var result = await userManager.AddOrUpdatePasskeyAsync(current, attestation.Passkey);
            return result.Succeeded ? SparkPasskeyOutcome.Done : SparkPasskeyOutcome.Refused;
        }
        catch (InvalidOperationException)
        {
            // The store refuses a credential id already held by another account, and refuses
            // without saying by whom. Reporting "already registered" would undo that.
            return SparkPasskeyOutcome.Refused;
        }
    }

    public async Task<SparkPasskeyOutcome> RenameAsync(string credentialId, string? name)
    {
        if (await UserAsync() is not { } current)
            return SparkPasskeyOutcome.Unavailable;

        if (!PasskeyEndpoints.TryDecodeCredentialId(credentialId, out var id))
            return SparkPasskeyOutcome.NotFound;

        var passkey = await userManager.GetPasskeyAsync(current, id);
        if (passkey is null)
            return SparkPasskeyOutcome.NotFound;

        passkey.Name = PasskeyEndpoints.Sanitize(name);
        var result = await userManager.AddOrUpdatePasskeyAsync(current, passkey);
        return result.Succeeded ? SparkPasskeyOutcome.Done : SparkPasskeyOutcome.Refused;
    }

    public async Task<SparkPasskeyOutcome> RemoveAsync(string credentialId)
    {
        if (await UserAsync() is not { } current)
            return SparkPasskeyOutcome.Unavailable;

        if (!PasskeyEndpoints.TryDecodeCredentialId(credentialId, out var id))
            return SparkPasskeyOutcome.NotFound;

        if (await userManager.GetPasskeyAsync(current, id) is null)
            return SparkPasskeyOutcome.NotFound;

        // Removing the last thing that can sign this account in locks the user out permanently, and
        // no amount of support undoes it.
        var logins = await userManager.GetLoginsAsync(current);
        var passkeys = await userManager.GetPasskeysAsync(current);
        if (SparkCredentialInventory.WouldRemoveLastPasskey(
                passkeys.Count,
                logins.Count,
                await userManager.HasPasswordAsync(current),
                options.Value.LocalCredentials,
                options.Value.Passkeys))
        {
            return SparkPasskeyOutcome.LastCredential;
        }

        var result = await userManager.RemovePasskeyAsync(current, id);
        return result.Succeeded ? SparkPasskeyOutcome.Done : SparkPasskeyOutcome.Refused;
    }
}
