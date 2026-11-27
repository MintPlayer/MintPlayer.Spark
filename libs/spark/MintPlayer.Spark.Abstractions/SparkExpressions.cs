using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace MintPlayer.Spark.Abstractions;

/// <summary>Building blocks for the query trees Spark hands a store provider.</summary>
public static class SparkExpressions
{
    private static readonly ConcurrentDictionary<Type, Func<object?, object>> Holders = new();

    /// <summary>
    /// <paramref name="value"/> as a captured field: a field read off a constant holder, which is what a C# closure
    /// compiles to (#536 S2b, PRD §8.10 finding 3). EF turns it into a query parameter, where an
    /// <see cref="Expression.Constant(object)"/> becomes a SQL literal and a query plan per value. RavenDB and LINQ to
    /// objects evaluate it as they evaluate any closure. Use it for every value that comes from a request.
    /// </summary>
    public static Expression Captured(object? value, Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var holder = Holders.GetOrAdd(type, static t =>
        {
            var holderType = typeof(CapturedValue<>).MakeGenericType(t);
            return v => Activator.CreateInstance(holderType, [v])!;
        })(value);
        return Expression.Field(Expression.Constant(holder), holder.GetType().GetField(nameof(CapturedValue<object>.Value), BindingFlags.Public | BindingFlags.Instance)!);
    }

    /// <summary>The holder <see cref="Captured(object?, Type)"/> reads its value from.</summary>
    private sealed class CapturedValue<T>(T value)
    {
        public readonly T Value = value;
    }
}
