using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Actions;

/// <summary>
/// Base class for the Actions of a JSON-only virtual type — a persistent object that exists in
/// <c>App_Data/Model/*.json</c> (no <c>clrType</c>) but never in the database. Because there is
/// no CLR entity, <see cref="DefaultPersistentObjectActions{T}"/> cannot apply; this base carries
/// the one hook such a type has: composition. The framework resolves the class by name —
/// <c>{Name}Actions</c> for the model file's <c>name</c> — exactly like entity Actions classes.
///
/// <example>
/// <code>
/// public partial class StartPageActions : SparkVirtualObjectActions
/// {
///     [Inject] private readonly IAsyncDocumentSession session;
///
///     public override async Task&lt;PersistentObject?&gt; OnComposeAsync(SparkComposeArgs args)
///     {
///         args.PersistentObject["Welcome"].Value = "Hello!";
///         args.PersistentObject.Breadcrumb = "Start";
///         return args.PersistentObject;
///     }
/// }
/// </code>
/// </example>
/// </summary>
public abstract class SparkVirtualObjectActions
{
    /// <summary>
    /// Composes the page served for this type: fill <c>args.PersistentObject</c>'s attribute
    /// values and <see cref="PersistentObject.Breadcrumb"/> (the page title) and return it,
    /// ignoring <see cref="SparkComposeArgs.RequestedId"/> at will. Runs under the type-level
    /// <c>Read</c> right; the result is served read-only. Returning <see langword="null"/> —
    /// the default — means the type has no page, and the request 404s.
    /// </summary>
    public virtual Task<PersistentObject?> OnComposeAsync(SparkComposeArgs args)
        => Task.FromResult<PersistentObject?>(null);
}
