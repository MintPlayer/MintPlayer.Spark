namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>
/// The words of the "do you want this login attached to your account?" message, as plain strings.
/// </summary>
/// <remarks>
/// <para>
/// <b>Plain strings on purpose, for now.</b> No templating engine, no razor view, no resource file:
/// a mail manager is planned separately, and until it exists the shortest thing that says the right
/// words beats a rendering pipeline with one message in it.
/// </para>
/// <para>
/// It lives in Spark rather than in each application because the <em>wording</em> is part of the
/// security design, not decoration. The reader who did not start this is the case that matters —
/// they are being told that somebody else's sign-in matched their address, and this mail is the only
/// place that can be said. An application left to phrase it alone tends to write a notification
/// ("your account has been linked"), which is both untrue at the time of sending and useless to the
/// person who needs to act.
/// </para>
/// <para>
/// ⚠️ <b>Spark still ships no transport.</b> These are strings; sending them is the application's
/// job, and <see cref="ISparkLinkConfirmationSender{TUser}"/> is what it implements. An application
/// is free to ignore this class and write its own text.
/// </para>
/// </remarks>
public static class SparkLinkConfirmationMessage
{
    /// <summary>The subject line.</summary>
    /// <param name="providerDisplayName">The provider as a human would name it — "GitLab".</param>
    /// <param name="applicationName">
    /// The application asking, when it has a name worth printing. Omitted, the subject stays
    /// generic rather than inventing one.
    /// </param>
    public static string Subject(string providerDisplayName, string? applicationName = null)
        => string.IsNullOrWhiteSpace(applicationName)
            ? $"Confirm adding {providerDisplayName} to your account"
            : $"Confirm adding {providerDisplayName} to your {applicationName} account";

    /// <summary>
    /// The body, as plain text.
    /// </summary>
    /// <param name="providerDisplayName">The provider as a human would name it.</param>
    /// <param name="providerIdentity">
    /// How the provider named the person ("octocat"), when it said. A reader who did not start this
    /// needs it to tell whose sign-in matched their address.
    /// </param>
    /// <param name="confirmationLink">The single-use link.</param>
    /// <param name="validFor">How long the link lasts, so the reader knows whether to hurry.</param>
    /// <param name="applicationName">The application asking, if it has a name.</param>
    /// <remarks>
    /// ⚠️ Reads as a <b>request that can be declined</b>, and says plainly that doing nothing is a
    /// valid answer. A message that reads as a notification of something already done leaves the
    /// person who did not start it with no action and no reason to report it.
    /// </remarks>
    public static string Body(
        string providerDisplayName,
        string? providerIdentity,
        string confirmationLink,
        TimeSpan validFor,
        string? applicationName = null)
    {
        var who = string.IsNullOrWhiteSpace(providerIdentity)
            ? $"Somebody signed in with {providerDisplayName}"
            : $"Somebody signed in with {providerDisplayName} as {providerIdentity}";
        // "your account" when there is no name to print, rather than a placeholder standing in for
        // one. "your this site account" is the kind of seam that makes a security mail look
        // automated enough to ignore.
        var whose = string.IsNullOrWhiteSpace(applicationName) ? "your" : $"your {applicationName}";
        var window = validFor.TotalHours >= 1
            ? $"{validFor.TotalHours:0.#} hour{(validFor.TotalHours >= 2 ? "s" : "")}"
            : $"{validFor.TotalMinutes:0} minutes";

        return $"""
            {who}, using the email address on {whose} account.

            Nobody has been signed in, and nothing has been added to your account yet. If that was
            you, confirm it here:

            {confirmationLink}

            The link works once and stops working after {window}.

            If it was not you, do nothing. The link will expire on its own and your account stays
            as it is. Somebody knowing your email address is not enough to get into it.
            """;
    }
}
