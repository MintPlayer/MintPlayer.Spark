namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Declares a transient lookup reference from an enum (commerce X20 item 5, G1). The source generator
/// emits a <c>sealed partial</c> <see cref="TransientLookupReference{TKey}"/> class with one item per
/// member, in declaration order (or <see cref="SparkLookupOrderAttribute"/>'s), whose label is the <c>translations.json</c> key
/// <c>lookup.{Enum}.{Member}</c>: the enum carries no text, the library's or application's
/// <c>translations.json</c> layer does.
/// </summary>
/// <remarks>
/// <code>
/// [SparkLookup]
/// public enum ECarStatus { Available, Sold }
/// // generates: public sealed partial class CarStatus : TransientLookupReference&lt;ECarStatus&gt;
/// // labels:    "lookup": { "ECarStatus": { "Available": { "en": "Available", ... }, "Sold": { ... } } }
/// </code>
/// <para>
/// The lookup's name, which model files refer to in <c>lookupReferenceType</c>, is
/// <see cref="Name"/>, by default the enum's name without its <c>E</c> prefix (<c>ECarStatus</c> →
/// <c>CarStatus</c>). Set <see cref="StringKeys"/> for a lookup whose keys are stored as strings: the
/// class then derives from <c>TransientLookupReference&lt;string&gt;</c> and carries a
/// <c>const string</c> per member, the member's name or its <see cref="SparkLookupKeyAttribute"/>.
/// </para>
/// <para>
/// Extra per-item data (an event name, a flag) goes in a partial declaration of the generated class:
/// declare the properties with a private setter and implement
/// <c>static partial void Initialize({Name} item)</c>, which runs once per item.
/// </para>
/// <para>
/// ⚠️ <c>tools/verify-translation-keys.test.mjs</c> fails the build when a member's key is missing from
/// the declaring project's <c>translations.json</c> layers, and when a <c>lookup.*</c> key names no member.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Enum, AllowMultiple = false, Inherited = false)]
public sealed class SparkLookupAttribute : Attribute
{
    public SparkLookupAttribute() { }

    /// <param name="name">The lookup's name: the generated class, and the name model files refer to.</param>
    public SparkLookupAttribute(string name) => Name = name;

    /// <summary>The lookup's name; null means the enum's name without its <c>E</c> prefix.</summary>
    public string? Name { get; }

    /// <summary>How the lookup is displayed. Default <see cref="ELookupDisplayType.Dropdown"/>.</summary>
    public ELookupDisplayType DisplayType { get; set; } = ELookupDisplayType.Dropdown;

    /// <summary>
    /// Whether the keys are strings (the member names, or their <see cref="SparkLookupKeyAttribute"/>)
    /// rather than the enum itself. For lookups whose values are already stored as strings.
    /// </summary>
    public bool StringKeys { get; set; }
}

/// <summary>
/// The stored key of a member of a <see cref="SparkLookupAttribute.StringKeys"/> lookup when it differs
/// from the member's name (<c>[SparkLookupKey("scoped")] Scoped</c>). The translation key stays
/// <c>lookup.{Enum}.{Member}</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class SparkLookupKeyAttribute(string key) : Attribute
{
    public string Key { get; } = key;
}

/// <summary>
/// The position of a member's item when the display order differs from the declaration order, which
/// often holds a meaning of its own (the zero value is the stored default). Items are sorted by this
/// number, ascending, unmarked members counting as 0; equal numbers keep declaration order.
/// </summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class SparkLookupOrderAttribute(int order) : Attribute
{
    public int Order { get; } = order;
}
