using System.Collections;
using System.Linq.Expressions;

namespace MintPlayer.Spark;

/// <summary>
/// Helpers the generated library-declared indexes call (commerce X19, <c>[SparkIndexMap]</c>).
/// </summary>
public static class SparkIndexMaps
{
    /// <summary>
    /// Retypes a library-declared map to the shape <c>AddMap&lt;TSource&gt;</c> takes.
    /// </summary>
    /// <remarks>
    /// A library declares its map with its result type, <c>IEnumerable&lt;TResult&gt;</c>, so it is checked against
    /// that type where it is written. <c>AddMap</c> wants <c>Expression&lt;Func&lt;IEnumerable&lt;TSource&gt;, IEnumerable&gt;&gt;</c>,
    /// and <see cref="Expression{TDelegate}"/> is a class, so the two are not convertible. The body and parameter
    /// are reused unchanged: <c>IEnumerable&lt;TResult&gt;</c> is reference-assignable to <c>IEnumerable</c>, which is
    /// all <see cref="Expression.Lambda{TDelegate}(Expression, ParameterExpression[])"/> requires, and the tree
    /// RavenDB translates into the index definition is the library's own.
    /// </remarks>
    public static Expression<Func<IEnumerable<TSource>, IEnumerable>> Widen<TSource, TResult>(
        Expression<Func<IEnumerable<TSource>, IEnumerable<TResult>>> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return Expression.Lambda<Func<IEnumerable<TSource>, IEnumerable>>(map.Body, map.Parameters);
    }
}
