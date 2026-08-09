namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// An entity whose document id is derived from its own contents rather than generated.
/// Spark stores it under <see cref="GetId"/> instead of the default
/// <c>{Collection}/{Guid}</c>.
/// <para>
/// The reason to want this is lookups. A generated id tells you nothing, so finding a car by
/// its licence plate means querying an index — and RavenDB index queries are eventually
/// consistent, so a document written moments ago may not be there yet. A derived id makes the
/// same lookup a point-load, which has no such window. That is why every id in this framework
/// that gates a security decision is derived.
/// </para>
/// <para>
/// The counterpart is a <c>static</c> method with the same derivation, so callers can compute
/// the id from the key alone — before they have a document to ask:
/// </para>
/// <code language="csharp">
/// public class Car : IHasNaturalId
/// {
///     public static string GetId(string licencePlate) => $"cars/{licencePlate.ToUpperInvariant()}";
///     string IHasNaturalId.GetId() => GetId(LicencePlate);
///
///     public string LicencePlate { get; set; } = null!;
/// }
///
/// // and then, instead of an index query:
/// var car = await session.LoadAsync&lt;Car&gt;(Car.GetId("1-ABC-234"));
/// </code>
/// <para>
/// Implement the interface member explicitly, as above, so the static overload is what shows up
/// on the type and the two cannot be confused at a call site.
/// </para>
/// <para>
/// <b>The id must not change</b> once the document exists. RavenDB has no rename: storing the
/// same entity after the derivation's inputs change writes a *second* document and leaves the
/// original behind. Derive from something the entity does not get to edit — a licence plate, an
/// external system's key, a pair of foreign keys — not from a display name.
/// </para>
/// </summary>
public interface IHasNaturalId
{
    /// <summary>
    /// This entity's document id, derived from its own properties. Called once, when the entity
    /// is first stored.
    /// </summary>
    string GetId();
}
