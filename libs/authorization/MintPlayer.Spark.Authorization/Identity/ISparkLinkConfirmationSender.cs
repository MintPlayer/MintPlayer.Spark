namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>
/// Sends the "do you want this login attached to your account?" message.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own contract rather than ASP.NET Identity's <c>IEmailSender&lt;TUser&gt;</c>.</b> That
/// interface's members are confirmation links, password resets and reset codes — a password-world
/// vocabulary with nothing that means "a second credential wants in". Adding this there would have
/// meant either bending <c>SendConfirmationLinkAsync</c> into saying something it does not say, or
/// widening an ASP.NET interface every application already implements.
/// </para>
/// <para>
/// ⚠️ <b>The address is not a parameter by accident.</b> The mail goes to the address <em>already
/// stored on the account</em>, which the implementation reads from the user — never to the one the
/// new provider just asserted. Mailing the asserted address would confirm nothing, because the
/// person requesting the link chose it. Taking it as a parameter would make that mistake possible
/// to write.
/// </para>
/// <para>
/// Spark ships this contract and no transport. An application that configures
/// <see cref="Configuration.SparkExternalLoginLinking.ConfirmByEmail"/> without registering one is
/// refused at startup, because the alternative is confirmations that are silently discarded and a
/// link that is never made.
/// </para>
/// </remarks>
/// <typeparam name="TUser">The application's user type.</typeparam>
public interface ISparkLinkConfirmationSender<in TUser> where TUser : SparkUser
{
    /// <summary>
    /// Asks the owner of <paramref name="user"/> to confirm attaching a new external login.
    /// </summary>
    /// <param name="user">
    /// The <em>existing</em> account. The implementation takes the destination address from this,
    /// not from anything the requesting provider supplied.
    /// </param>
    /// <param name="providerDisplayName">
    /// How to name the provider in the message — "GitLab" rather than a scheme id, because the
    /// reader has to recognise it to make a sensible decision.
    /// </param>
    /// <param name="providerIdentity">
    /// How the provider identified the person ("octocat"), so a reader who did not initiate this
    /// can tell whose account is asking and report it rather than ignore it.
    /// </param>
    /// <param name="confirmationLink">The single-use link that completes the attachment.</param>
    /// <remarks>
    /// ⚠️ The message must read as a <em>request</em> that can be declined, not as a notification of
    /// something already done. A reader who did not start this is the case that matters: they are
    /// being told someone else's sign-in matched their address, and the mail is the only place that
    /// can be said.
    /// </remarks>
    Task SendLinkConfirmationAsync(
        TUser user,
        string providerDisplayName,
        string? providerIdentity,
        string confirmationLink,
        CancellationToken cancellationToken = default);
}
