using System.Text.Json;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Authorization.Actions;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.CustomActions;

/// <summary>
/// Enrolls a passkey from the <c>Passkeys</c> page: a WebAuthn ceremony run in the browser through a
/// client-method retry (PRD D3, D7), with authorization, ceremony state and the follow-up all inside
/// this one action.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pass 1</b> asks <see cref="ISparkPasskeyAccount"/> for the creation options, which also sets the
/// ceremony's DataProtection-protected state cookie on the response, and hands them to the browser's
/// <c>webauthn.create</c>; the response is a 449 that carries the cookie (S1). <b>Pass 2</b> is the
/// same request with the browser's credential as the retry's <c>Value</c>, attested against that
/// cookie.
/// </para>
/// <para>
/// ⚠️ <b>The options are built inside the factory, never before <c>Invoke</c>.</b> The action re-runs
/// from the top on pass 2; options made there would issue a new challenge and overwrite the cookie,
/// and the attestation would fail. <c>Invoke</c> runs its factory only while the step is unanswered.
/// </para>
/// <para>
/// No CSRF refresh afterwards: registering changes no claim antiforgery binds to, and the 449 already
/// re-issued <c>XSRF-TOKEN</c> (S5). Its <c>actions.json</c> entry says <c>"requiresClient":
/// "webauthn.create"</c>, so a browser without passkeys sees the button disabled with a reason.
/// </para>
/// </remarks>
internal sealed partial class AddPasskeyAction : ICustomAction
{
    /// <summary>The client method <c>ng-spark-auth</c> registers.</summary>
    internal const string ClientMethod = "webauthn.create";

    [Inject] private readonly IManager manager;
    [Inject] private readonly ISparkPasskeyAccount passkeys;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        // Side-effect free, so it may run on both passes.
        if (!await passkeys.IsAvailableAsync())
        {
            manager.Client.Notify(manager.GetTranslatedMessage("auth.passkeyFailed"), NotificationKind.Error);
            return;
        }

        await manager.Retry.Invoke(ClientMethod, async () => (object?)await passkeys.CreationOptionsAsync());

        // A dismissed authenticator prompt, an unsupported browser or an unregistered method: the user
        // chose not to, which is not an error worth a banner (G5).
        var answer = manager.Retry.Result!;
        if (answer.Option == RetryResult.CancelOption || answer.Value is not { ValueKind: JsonValueKind.Object } credential)
            return;

        // The browser reports a ceremony that failed for a reason other than the user's choice (an
        // authenticator that already holds a credential for this account, for instance) as an error
        // code instead of a credential, so the user hears that it did not work.
        if (credential.TryGetProperty("error", out _))
        {
            manager.Client.Notify(manager.GetTranslatedMessage("auth.passkeyFailed"), NotificationKind.Error);
            return;
        }

        // The challenge made on pass 1 is reused here by design, and it is time-bound: the state cookie
        // lives 5 minutes. A pass 2 after that (or without the cookie) is Expired, said as "try again"
        // and nothing more; every other refusal stays the uniform "did not work".
        var outcome = await passkeys.RegisterAsync(credential.GetRawText());
        if (outcome != SparkPasskeyOutcome.Done)
        {
            var message = outcome == SparkPasskeyOutcome.Expired ? "auth.passkeyExpired" : "auth.passkeyFailed";
            manager.Client.Notify(manager.GetTranslatedMessage(message), NotificationKind.Error);
            return;
        }

        manager.Client.Notify(manager.GetTranslatedMessage("auth.passkeyAddedNotice"), NotificationKind.Success);
        manager.Client.RefreshQuery(SparkPasskeysPage.QueryAlias);
    }
}
