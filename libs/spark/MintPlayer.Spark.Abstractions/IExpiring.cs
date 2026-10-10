namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// An entity that expires: past <see cref="ExpiresAtUtc"/> it is hidden from every Spark read path, and
/// RavenDB deletes it at its next expiration sweep (<c>docs/seo_analyzer_PRD.md</c> X1).
/// </summary>
/// <remarks>
/// <para>
/// <b>The property is the source of truth.</b> On every store, through the persistent-object pipeline
/// or a raw session alike, the framework copies it into the document's <c>@expires</c> metadata (and
/// removes <c>@expires</c> when it is null). A type whose model declares <c>"expiresAfter"</c> gets
/// <c>now + expiresAfter</c> when a <b>new</b> document is stored with no value; code that sets the
/// property itself — before the first store or on any later one — overrides that, and setting it to
/// null on an existing document makes it permanent.
/// </para>
/// <para>
/// Implement it as a <b>public</b> property, never explicitly: the read filter is rebound onto the
/// entity by member name, and RavenDB stores only public properties. Startup refuses a model type that
/// gets this wrong.
/// </para>
/// <para>
/// ⚠️ Deletion is housekeeping, not the guarantee. On the Community licence RavenDB sweeps at most every
/// 36 hours, so an expired document can linger that long; it is the read filter that makes it behave
/// as missing (a load answers 404, #453). A raw session query sees it until the sweep — filter with
/// <c>!(x.ExpiresAtUtc &lt;= now)</c> there, never <c>x.ExpiresAtUtc == null || …</c>, which drops
/// documents stored before the field existed (an absent field matches no <c>== null</c>).
/// </para>
/// </remarks>
public interface IExpiring
{
    /// <summary>When the document expires (UTC), or null for never.</summary>
    DateTime? ExpiresAtUtc { get; set; }
}
