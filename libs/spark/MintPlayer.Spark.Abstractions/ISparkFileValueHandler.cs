using System.Reflection;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Applies the posted value of a stored-file attribute (a <see cref="SparkFile"/>/<see cref="SparkImage"/>
/// property, or a list of them) to the entity being saved (#700). Core defines it; the
/// <c>MintPlayer.Spark.Files</c> plugin (<c>spark.AddFiles()</c>) implements it. Without the plugin a
/// model with a stored-file attribute is refused at startup.
/// </summary>
/// <remarks>
/// <para>
/// The posted value is <b>never</b> copied into the entity. The handler reads it as one of:
/// <list type="bullet">
/// <item><see langword="null"/>: clear.</item>
/// <item>an object carrying an upload token: claim that upload (bound to this user, type and attribute).</item>
/// <item>an object carrying a <c>fileId</c> the entity already holds: keep it (for a list, at this position).</item>
/// </list>
/// Anything else, including a <c>fileId</c> the entity does not hold, is a validation error.
/// </para>
/// <para>
/// It runs only on the asynchronous save path, which has a session: a claim reads and consumes a
/// staged upload. The synchronous <c>PopulateObjectValues</c> refuses a file attribute rather than
/// dropping it.
/// </para>
/// </remarks>
public interface ISparkFileValueHandler
{
    /// <summary>
    /// Called once per save, before any value of <see cref="SparkFileBeginContext.Root"/> is written: notes the files its
    /// AsDetail rows hold now, so files of a row the save drops are removed and a row cannot keep a file it does not hold.
    /// </summary>
    Task BeginAsync(SparkFileBeginContext context, CancellationToken cancellationToken);

    /// <summary>Applies <see cref="SparkFileWriteContext.Attribute"/>'s posted value to the entity.</summary>
    Task WriteAsync(SparkFileWriteContext context, CancellationToken cancellationToken);

    /// <summary>
    /// After the save stored the entity and before it commits: writes the bytes side of what
    /// <see cref="WriteAsync"/> decided (claimed files attached, dropped files removed) into the same unit of
    /// work. Separate from <see cref="WriteAsync"/> because a created entity has no id until it is stored.
    /// </summary>
    Task OnStoredAsync(SparkFileStoredContext context, CancellationToken cancellationToken);
}

/// <summary>What <see cref="ISparkFileValueHandler.OnStoredAsync"/> works on.</summary>
/// <param name="Entity">The stored entity: the instance <see cref="ISparkFileValueHandler.WriteAsync"/> wrote to.</param>
/// <param name="Id">Its document id, assigned by the store.</param>
/// <param name="Session">The save's session; nothing has been committed yet.</param>
public sealed record SparkFileStoredContext(object Entity, string Id, ISparkSession Session);

/// <summary>What <see cref="ISparkFileValueHandler.WriteAsync"/> works on.</summary>
/// <param name="Entity">The entity being saved, loaded from the store (an edit) or new (a create). Its current value is the one the save may keep.</param>
/// <param name="Property">The <see cref="SparkFile"/>/<see cref="SparkImage"/> (or list) property.</param>
/// <param name="Attribute">The posted attribute. Its <c>Value</c> is client input.</param>
/// <param name="Definition">The attribute's model definition, with its <c>fileOptions</c>; <see langword="null"/> for a type without a model registration.</param>
/// <param name="EntityTypeName">The model type's name (<c>Car</c>), to which a claim is bound.</param>
/// <param name="Session">The save's session.</param>
/// <param name="Owner">For a file inside an AsDetail row, the document the row lives in and the attribute path
/// (<c>ServiceEntries.Invoice</c>); a claim is bound to those instead. <see langword="null"/> for a top-level attribute.</param>
public sealed record SparkFileWriteContext(
    object Entity,
    PropertyInfo Property,
    PersistentObjectAttribute Attribute,
    EntityAttributeDefinition? Definition,
    string? EntityTypeName,
    ISparkSession Session,
    SparkFileOwner? Owner = null);

/// <summary>The document an AsDetail row's file belongs to (#700 M10).</summary>
/// <param name="Root">The entity being saved, which holds the row.</param>
/// <param name="RootTypeName">Its model type's name (<c>Car</c>).</param>
/// <param name="AttributePath">The AsDetail attribute and the row's file attribute, dotted: <c>ServiceEntries.Invoice</c>.</param>
public sealed record SparkFileOwner(object Root, string? RootTypeName, string AttributePath);

/// <summary>What <see cref="ISparkFileValueHandler.BeginAsync"/> works on.</summary>
/// <param name="Root">The entity about to be written, as loaded (an edit) or new (a create).</param>
/// <param name="Session">The save's session.</param>
public sealed record SparkFileBeginContext(object Root, ISparkSession Session);
