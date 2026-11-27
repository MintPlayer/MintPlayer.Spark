namespace MintPlayer.Spark.Abstractions.Storage;

/// <summary>
/// Writes the schema migration for a model change (#536 D24, O17): what <c>--spark-synchronize-model</c> runs after it
/// wrote the model JSON, on a provider with a schema. It lives in a design-time assembly that stays out of every
/// publish, which core loads by the name the provider registers (<see cref="SparkSchemaDesignAssembly"/>) and finds
/// through <see cref="SparkSchemaScaffolderAttribute"/>; the app never names it in code.
/// </summary>
public interface ISparkSchemaScaffolder
{
    /// <summary>
    /// Compares the store's model with the app's last migration and, when they differ, writes a migration into
    /// <see cref="SparkScaffoldRequest.ProjectDirectory"/>. Never touches a database.
    /// </summary>
    Task<SparkScaffoldResult> ScaffoldAsync(IServiceProvider services, SparkScaffoldRequest request, CancellationToken cancellationToken);
}

/// <summary>Where the migration goes.</summary>
/// <param name="ProjectDirectory">The host project's directory (the content root in development).</param>
/// <param name="Name">The migration's name; <see langword="null"/> for one derived from the time.</param>
public sealed record SparkScaffoldRequest(string ProjectDirectory, string? Name);

/// <summary>What the scaffolder did.</summary>
/// <param name="Files">The files it wrote; empty when the model had no change.</param>
/// <param name="Operations">A one-line summary per operation in the migration's <c>Up</c>, for the developer to review.</param>
/// <param name="Warnings">EF's warnings, such as possible data loss.</param>
public sealed record SparkScaffoldResult(IReadOnlyList<string> Files, IReadOnlyList<string> Operations, IReadOnlyList<string> Warnings)
{
    public static SparkScaffoldResult NoChanges { get; } = new([], [], []);
}

/// <summary>
/// The design-time assembly that holds a provider's <see cref="ISparkSchemaScaffolder"/>, registered by the provider
/// (the SQL providers: <c>MintPlayer.Spark.Relational.Design</c>).
/// </summary>
/// <param name="AssemblyName">The assembly's name, loaded from the app's <c>bin</c>.</param>
public sealed record SparkSchemaDesignAssembly(string AssemblyName);

/// <summary>Names the <see cref="ISparkSchemaScaffolder"/> a design-time assembly provides.</summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class SparkSchemaScaffolderAttribute : Attribute
{
    public SparkSchemaScaffolderAttribute(Type scaffolderType) => ScaffolderType = scaffolderType;

    /// <summary>A class with a public parameterless constructor that implements <see cref="ISparkSchemaScaffolder"/>.</summary>
    public Type ScaffolderType { get; }
}
