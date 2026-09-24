using Microsoft.Playwright;

namespace MintPlayer.Spark.E2E.Tests._Infrastructure;

/// <summary>
/// A software WebAuthn authenticator attached to a browser context, via Chrome DevTools Protocol.
/// </summary>
/// <remarks>
/// <para>
/// Without this a passkey test cannot exist: <c>navigator.credentials.create()</c> needs a real
/// authenticator, and there is no way to hand one a scripted gesture. CDP's <c>WebAuthn</c> domain
/// supplies one that consents automatically, so the ceremony runs end to end with the browser doing
/// the real CBOR/COSE work rather than a fixture posting JSON we wrote ourselves.
/// </para>
/// <para>
/// ⚠️ The option values are not arbitrary — they have to agree with the server's
/// <c>IdentityPasskeyOptions</c>, which Spark pins. <c>hasResidentKey</c> because sign-in is
/// discoverable-credential only (the request carries no username, so the authenticator must be able
/// to produce the credential unprompted), and <c>hasUserVerification</c> +
/// <c>isUserVerified</c> because the server asks for <c>userVerification: "required"</c> and would
/// otherwise reject every assertion.
/// </para>
/// </remarks>
public sealed class VirtualAuthenticator : IAsyncDisposable
{
    private readonly ICDPSession _session;
    private readonly string _authenticatorId;

    private VirtualAuthenticator(ICDPSession session, string authenticatorId)
    {
        _session = session;
        _authenticatorId = authenticatorId;
    }

    public static async Task<VirtualAuthenticator> AttachAsync(IPage page)
    {
        var session = await page.Context.NewCDPSessionAsync(page);
        await session.SendAsync("WebAuthn.enable");

        var result = await session.SendAsync("WebAuthn.addVirtualAuthenticator", new Dictionary<string, object>
        {
            ["options"] = new Dictionary<string, object>
            {
                ["protocol"] = "ctap2",
                ["transport"] = "internal",
                ["hasResidentKey"] = true,
                ["hasUserVerification"] = true,
                // Consent without a human. The prompt would otherwise block forever in CI.
                ["isUserVerified"] = true,
                ["automaticPresenceSimulation"] = true,
            },
        });

        var id = result!.Value.GetProperty("authenticatorId").GetString()
            ?? throw new InvalidOperationException("CDP did not return an authenticatorId.");

        return new VirtualAuthenticator(session, id);
    }

    /// <summary>
    /// How many credentials the authenticator holds — the ground truth that a passkey was really
    /// created on the device, rather than merely recorded server-side.
    /// </summary>
    public async Task<int> CredentialCountAsync()
    {
        var result = await _session.SendAsync("WebAuthn.getCredentials", new Dictionary<string, object>
        {
            ["authenticatorId"] = _authenticatorId,
        });

        return result!.Value.GetProperty("credentials").GetArrayLength();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _session.SendAsync("WebAuthn.removeVirtualAuthenticator", new Dictionary<string, object>
            {
                ["authenticatorId"] = _authenticatorId,
            });
        }
        catch
        {
            // The context may already be closed by PageFactory; a leaked virtual authenticator dies
            // with the browser either way, so failing here would only mask the test's own result.
        }

        await _session.DetachAsync();
    }
}
