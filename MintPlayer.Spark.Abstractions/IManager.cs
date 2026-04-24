using MintPlayer.Spark.Abstractions.Retry;

namespace MintPlayer.Spark.Abstractions;

public interface IManager
{
    /// <summary>
    /// Scaffolds a blank PersistentObject for the entity type registered under
    /// <paramref name="name"/>. All declared attributes are created with full
    /// metadata (DataType, Label, Rules, Renderer, ShowedOn, Order, Group,
    /// IsRequired/Visible/ReadOnly/Array, Query for References); Value is null.
    /// Throws <see cref="KeyNotFoundException"/> on unknown or ambiguous name —
    /// prefer the <see cref="GetPersistentObject(Guid)"/> overload in apps that
    /// declare entities across multiple database schemas.
    /// </summary>
    /// <remarks>
    /// This is the idiomatic way to build a PO for a popup / dialog / form —
    /// declare the shape as a Virtual PO in <c>App_Data/Model/*.json</c> and
    /// look it up by name, rather than constructing the PO and its attributes
    /// by hand.
    /// </remarks>
    PersistentObject GetPersistentObject(string name);

    /// <summary>
    /// Scaffolds a blank PersistentObject by ObjectTypeId. Unambiguous — preferred
    /// over the name overload whenever the caller already has the Guid
    /// (e.g. from the source-generated <c>PersistentObjectIds</c> constants).
    /// </summary>
    PersistentObject GetPersistentObject(Guid id);

    /// <summary>
    /// Scaffolds a blank PersistentObject for <typeparamref name="T"/>, resolving the
    /// ObjectTypeId by looking up <c>typeof(T).FullName</c> against the registered
    /// EntityTypeDefinitions. The cleanest path when the caller has a typed entity
    /// class — no Guid plumbing, no string names.
    /// </summary>
    PersistentObject GetPersistentObject<T>() where T : class;

    /// <summary>
    /// Access to the Retry Action subsystem.
    /// </summary>
    IRetryAccessor Retry { get; }

    /// <summary>
    /// Gets a translated message for the current request culture, with placeholder substitution.
    /// Looks up the key in translations.json and calls string.Format with the provided parameters.
    /// </summary>
    string GetTranslatedMessage(string key, params object[] parameters);

    /// <summary>
    /// Gets a translated message for a specific language, with placeholder substitution.
    /// Looks up the key in translations.json and calls string.Format with the provided parameters.
    /// </summary>
    string GetMessage(string key, string language, params object[] parameters);
}
