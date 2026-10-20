namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Renames a persistent-object type a referenced library ships (D28, X9): the application states it
/// once, and every layer of the library is read under the new name — its model file, its queries'
/// <c>entityType</c>, the targets of its rights and its <c>model.*</c> translation keys. The old name
/// is then free for a type of the application's own.
/// </summary>
/// <remarks>
/// <para>
/// Optional: without it a type is named as it always was (a library type by its declared name, an
/// application CLR type by its class name). Use the generic form for a type with a CLR class
/// (<c>[assembly: PersistentObjectName&lt;Commerce.Order&gt;("WebshopOrder")]</c>), which also names
/// an application's own CLR type; use this form for a library type without a CLR class, by the
/// library's alias and the name it declares (<c>[assembly: PersistentObjectName("authorization",
/// "Passkeys", "MyPasskeys")]</c>).
/// </para>
/// <para>
/// A type's id never changes with its name: a library id is derived from <c>alias:DeclaredName</c>.
/// Only an application may rename (SPARK061), and the application's model file for a renamed
/// library type is a delta under the new name, anchored by <c>"$extends": "alias:DeclaredName"</c>.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public class PersistentObjectNameAttribute : Attribute
{
    /// <param name="library">The library's <c>SparkLibraryAlias</c>, e.g. <c>authorization</c>.</param>
    /// <param name="declaredName">The name the library declares the type under (<c>persistentObject.name</c> in its file).</param>
    /// <param name="name">The name the application uses.</param>
    public PersistentObjectNameAttribute(string library, string declaredName, string name)
    {
        Library = library;
        DeclaredName = declaredName;
        Name = name;
    }

    /// <summary>For <see cref="PersistentObjectNameAttribute{T}"/>: the type is named by its CLR class.</summary>
    protected PersistentObjectNameAttribute(string name)
    {
        Name = name;
    }

    /// <summary>The library's alias; <see langword="null"/> for the generic form.</summary>
    public string? Library { get; }

    /// <summary>The name the library declares; <see langword="null"/> for the generic form.</summary>
    public string? DeclaredName { get; }

    /// <summary>The name the type has in this application.</summary>
    public string Name { get; }

    /// <summary>The CLR class the generic form names; <see langword="null"/> for this form.</summary>
    public virtual Type? EntityType => null;
}

/// <summary>
/// Names the persistent-object type of <typeparamref name="T"/>: a library's CLR type (renamed in
/// every layer of its library, D28) or one of the application's own (instead of its class name).
/// </summary>
/// <typeparam name="T">The entity's CLR class.</typeparam>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class PersistentObjectNameAttribute<T> : PersistentObjectNameAttribute where T : class
{
    /// <param name="name">The name the type has in this application.</param>
    public PersistentObjectNameAttribute(string name) : base(name) { }

    public override Type EntityType => typeof(T);
}
