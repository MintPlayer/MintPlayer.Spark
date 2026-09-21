namespace CodeCoverage.Services;

/// <summary>
/// Where this deployment hands outgoing mail to, and who it says it is from.
/// </summary>
/// <remarks>
/// <para>
/// The app does not talk to the internet. It hands a message to the <c>coverage-smtp</c> container
/// on the internal Docker network, which queues it and does the delivering — so there is no TLS, no
/// credential and no timeout tuning here, and none of those belong in application configuration.
/// </para>
/// <para>
/// ⚠️ That split is not tidiness. The send happens <em>inside the external-login callback</em>,
/// while someone is waiting on an HTTP response. A handoff to a container one hop away takes
/// milliseconds and succeeds whether or not the receiving mail server is reachable; talking to a
/// remote SMTP server directly would put an internet round-trip — and its timeouts — in the middle
/// of a sign-in.
/// </para>
/// </remarks>
public sealed class CoverageMailOptions
{
    /// <summary>The relay's hostname on the internal network. Unset disables outgoing mail.</summary>
    /// <remarks>
    /// Unset is a supported state, and the one the deployment is in by default: with no sender
    /// registered, Spark refuses to start under
    /// <c>SparkExternalLoginLinking.ConfirmByEmail</c> and runs normally under the others.
    /// </remarks>
    public string? Host { get; set; }

    /// <summary>Submission port. 587 is what the relay container listens on.</summary>
    public int Port { get; set; } = 587;

    /// <summary>
    /// The envelope and header From.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This address decides whether the mail is delivered at all.</b> It must be at a domain
    /// whose SPF authorises this server and whose DKIM key the relay signs with — see the README's
    /// deployment section. <c>mintplayer.com</c> publishes <c>sp=reject</c>, so a subdomain address
    /// that is not set up properly is <em>rejected</em> rather than junked.
    /// </remarks>
    public string? FromAddress { get; set; }

    /// <summary>The display name beside <see cref="FromAddress"/>.</summary>
    public string FromName { get; set; } = "MintPlayer Coverage";

    /// <summary>How the application names itself in the message. Blank keeps the text generic.</summary>
    public string ApplicationName { get; set; } = "Coverage";

    /// <summary>Whether enough is configured to send anything.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);
}
