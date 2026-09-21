namespace CodeCoverage.Feedback;

/// <summary>
/// The hidden first line of every coverage comment we post, and the only thing that identifies one
/// as ours.
/// </summary>
/// <remarks>
/// <para>
/// It lives here, apart from the renderer that writes it, because <b>two sides need it and they
/// belong in different assemblies</b>. The renderer is forge-neutral presentation and stays with
/// the application; the publisher is forge-specific transport and moves into the forge integration,
/// where it uses this to re-adopt its own comment when the stored id is gone — which is what keeps
/// "one comment per pull request" true rather than merely intended.
/// </para>
/// <para>
/// ⚠️ <b>Changing this string orphans every comment already posted.</b> The publisher would stop
/// recognising them, stop editing them, and start posting a second comment on every pull request
/// that already has one. It is a wire format, not a constant.
/// </para>
/// <para>
/// An HTML comment, so it is invisible in rendered Markdown on every forge we target.
/// </para>
/// </remarks>
public static class CoverageCommentMarker
{
    /// <summary>The marker itself. See the type remarks before changing it.</summary>
    public const string Value = "<!-- coverage-bot:pr-summary -->";
}
