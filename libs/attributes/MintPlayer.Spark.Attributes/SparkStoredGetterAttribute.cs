using System.ComponentModel;

namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Generated, never written by hand: names a get-only member that is <b>stored</b>, and the hash of its formula
/// (#536 O13).
/// <para>
/// RavenDB's serializer writes every public getter into the document, so a computed member such as
/// <c>FullName =&gt; $"{FirstName} {LastName}"</c> can be filtered and sorted there. A SQL table needs a column to
/// do the same. <c>MintPlayer.Spark.LibraryGenerators</c> gives each such member a private backing field named
/// <see cref="FieldPrefix"/> + the member's name; the SQL providers map the member to it, so EF reads the getter
/// when it saves (the column always holds the current value) and LINQ on the member reads the column.
/// </para>
/// <para>
/// <see cref="Formula"/> hashes the getter's body (its tokens, so formatting and comments don't count).
/// <c>--spark-synchronize-model</c> records it in <c>modelHashes.json</c> and names the getters whose formula
/// changed: rows saved before the change keep the old value until they are saved again, on every store, and
/// recomputing them is a data migration the developer chooses.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class SparkStoredGetterAttribute : Attribute
{
    /// <summary>The generated backing field's name is this prefix plus the member's name.</summary>
    public const string FieldPrefix = "__stored_";

    public SparkStoredGetterAttribute(string property, string formula)
    {
        Property = property;
        Formula = formula;
    }

    /// <summary>The get-only member's name.</summary>
    public string Property { get; }

    /// <summary>The SHA-256 of the getter body's tokens, lower-case hex.</summary>
    public string Formula { get; }
}
