namespace MintPlayer.Spark.Models;

/// <summary>
/// An optional, declarative condition deciding whether a custom action is <em>offered</em> on a
/// given object.
/// </summary>
/// <remarks>
/// <para>
/// <b>Presentation, never authorization.</b> The action list is a type-level catalogue —
/// <c>GET /spark/actions/{objectTypeId}</c> is not told which row is open — so this is evaluated on
/// the client, against the object it is already displaying. A client can therefore ignore it, which
/// is exactly why the action's own handler must still refuse: this hides a button, it does not
/// protect anything.
/// </para>
/// <para>
/// It exists because "offered to everyone, refused by the handler" is correct and still a bad
/// experience. Coverage's <c>DeleteData</c> is the case that prompted it: an irreversible, red
/// button sat on every repository page including healthy ones, and only told you it would refuse
/// after you had confirmed the deletion. Rights in that app are group-level with no group per
/// GitHub owner, so the right cannot express "only for a disconnected repository" — and it should
/// not have to, because that is a property of the row, not of the caller.
/// </para>
/// </remarks>
public class CustomActionVisibility
{
    /// <summary>The attribute on the displayed object to test, by name.</summary>
    public required string Attribute { get; set; }

    /// <summary>
    /// Show the action only when the attribute equals this value, compared as text and
    /// case-insensitively so that an enum reaching the client as a name still matches.
    /// </summary>
    public string? Equals { get; set; }

    /// <summary>
    /// Show the action only when the attribute does <b>not</b> equal this value.
    /// <para>
    /// Both may be set, in which case both must hold. Neither being set is a no-op rather than an
    /// error: a half-written rule should not silently hide an action.
    /// </para>
    /// </summary>
    public string? NotEquals { get; set; }
}
