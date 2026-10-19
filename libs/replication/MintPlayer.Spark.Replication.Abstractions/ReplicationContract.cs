using System.Linq.Expressions;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.Replication.Abstractions;

/// <summary>
/// A replication contract the owning module publishes: a permanent lower-kebab name, a version, the
/// source type it reads, the record consumers receive, and the C# projection from one to the other
/// (warehouse WX2, commerce X20 item 7). Declared with
/// <see cref="Configuration.SparkReplicationOptions.Publish{TSource, TContract}(string, int, Expression{Func{TSource, TContract}})"/>.
/// <para>
/// The owner turns the projection into its RavenDB ETL transform itself. A consumer names only the
/// contract and version it reads, so no consumer-written JavaScript ever runs on the owner's server
/// (F16). A projection the ETL transform cannot express makes the contract HTTP-only (Q-W17).
/// </para>
/// </summary>
public sealed partial class ReplicationContract
{
    internal ReplicationContract(string name, int version, Type sourceType, Type contractType, LambdaExpression projection)
    {
        Name = name;
        Version = version;
        SourceType = sourceType;
        ContractType = contractType;
        Projection = projection;
    }

    /// <summary>The contract's permanent lower-kebab name, e.g. <c>cars</c> or <c>fulfilment-orders</c>.</summary>
    public string Name { get; }

    /// <summary>The contract version. Versions of one contract run side by side.</summary>
    public int Version { get; }

    /// <summary>The owner's document type the contract reads.</summary>
    public Type SourceType { get; }

    /// <summary>The record consumers receive. A consumer's replica type has this shape.</summary>
    public Type ContractType { get; }

    /// <summary>The projection from <see cref="SourceType"/> to <see cref="ContractType"/>.</summary>
    public LambdaExpression Projection { get; }

    /// <summary>
    /// Whether <paramref name="name"/> is a valid contract name: lower-kebab (<c>a-z</c>, digits,
    /// single hyphens between segments), starting with a letter.
    /// </summary>
    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    [GeneratedRegex("^[a-z][a-z0-9]*(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
