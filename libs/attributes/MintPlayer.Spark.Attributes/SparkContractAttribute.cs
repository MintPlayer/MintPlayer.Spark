namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Marks a type whose JSON the TypeScript client reads or writes: a response record, a request body, an
/// enum on the wire. The contracts generator (commerce X20 item 2, <c>docs/guide-typescript-contracts.md</c>)
/// emits it, and every type its members reach, as TypeScript into the module that claims its namespace
/// (<see cref="SparkClientModuleAttribute"/>).
/// </summary>
/// <remarks>
/// <para>
/// The TypeScript follows the wire, not the C# spelling: member names through the camelCase policy (or
/// <c>[JsonPropertyName]</c>), an enum as a number unless a <c>JsonStringEnumConverter</c> writes its names,
/// <c>long</c> and <c>decimal</c> as <c>number</c>, a nullable member as <c>name?: T | null</c>.
/// </para>
/// <para>
/// A type serialized by a custom <c>JsonConverter</c> is refused, because its properties need not be its
/// wire shape. Marking such a type with this attribute states that the converter writes exactly the
/// properties (as <c>PersistentObjectAttribute</c>'s does); a converter that writes something else gets its
/// TypeScript by hand, declared with <see cref="SparkClientTypeAttribute"/>.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum, AllowMultiple = false, Inherited = false)]
public sealed class SparkContractAttribute : Attribute
{
    /// <summary>Exports the type under its own name.</summary>
    public SparkContractAttribute() { }

    /// <summary>Exports the type under <paramref name="name"/>, for a TypeScript name the client already uses.</summary>
    public SparkContractAttribute(string name) => Name = name;

    /// <summary>The TypeScript name; null for the C# name.</summary>
    public string? Name { get; }
}

/// <summary>
/// Claims a C# namespace for one TypeScript module: every contract type in the namespace (or below it)
/// is emitted into that module's <c>generated/contracts.ts</c>. A contract type no module claims is an
/// error, so a new namespace cannot silently fall out of the client.
/// </summary>
/// <remarks>
/// A library names an entry point of its npm package (<c>@mintplayer/ng-spark/models</c>, written to
/// <c>libs/node_packages/ng-spark/models/src/generated/contracts.ts</c>); an application names
/// <see cref="Application"/> (written to <c>ClientApp/src/app/generated/contracts.ts</c>). The longest
/// claiming namespace wins.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class SparkClientModuleAttribute : Attribute
{
    /// <summary>The module of an application's own client: <c>ClientApp/src/app/generated/contracts.ts</c>.</summary>
    public const string Application = "app";

    /// <param name="namespace">The C# namespace claimed, with every namespace below it.</param>
    /// <param name="module">The module's import specifier, e.g. <c>@mintplayer/ng-spark/models</c>, or <see cref="Application"/>.</param>
    public SparkClientModuleAttribute(string @namespace, string module)
    {
        Namespace = @namespace;
        Module = module;
    }

    public string Namespace { get; }
    public string Module { get; }
}

/// <summary>
/// Declares the hand-written TypeScript of a type whose wire shape is not its properties (a custom
/// converter, such as <c>TranslatedString</c>'s). The contracts generator references it instead of emitting
/// it; the module that claims the type's namespace owns the file.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class SparkClientTypeAttribute : Attribute
{
    /// <param name="type">The C# type.</param>
    /// <param name="name">The exported TypeScript name.</param>
    /// <param name="file">The file that exports it, relative to the module's source folder and without extension, e.g. <c>translated-string</c>.</param>
    public SparkClientTypeAttribute(Type type, string name, string file)
    {
        Type = type;
        Name = name;
        File = file;
    }

    public Type Type { get; }
    public string Name { get; }
    public string File { get; }
}
