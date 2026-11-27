using System.Linq.Expressions;
using System.Reflection;

namespace MintPlayer.Spark.Abstractions.Storage;

/// <summary>
/// The assignments of <see cref="ISparkSession.UpdateWhereAsync{T}"/>: members of <typeparamref name="T"/> set to
/// constants. A value computed from the row has no set-based form on every provider; load, change and save instead.
/// </summary>
public sealed class SparkSetters<T> where T : class
{
    private readonly List<SparkSetter> setters = [];

    public IReadOnlyList<SparkSetter> Setters => setters;

    /// <summary>Sets <paramref name="member"/> (a property, or a path through properties: <c>x =&gt; x.Address.City</c>).</summary>
    public SparkSetters<T> Set<TValue>(Expression<Func<T, TValue>> member, TValue value)
    {
        setters.Add(new SparkSetter(SparkSetter.MembersOf(member), value, typeof(TValue)));
        return this;
    }
}

/// <summary>One assignment: the member path from the row, and the value.</summary>
public sealed record SparkSetter(IReadOnlyList<PropertyInfo> Members, object? Value, Type ValueType)
{
    /// <summary>The path as dotted member names (<c>Address.City</c>).</summary>
    public string Path => string.Join('.', Members.Select(m => m.Name));

    /// <summary>Writes <see cref="Value"/> into <paramref name="row"/>; a null object on the way is an error, as in a database patch.</summary>
    public void ApplyTo(object row)
    {
        var target = row;
        foreach (var member in Members.Take(Members.Count - 1))
            target = member.GetValue(target) ?? throw new InvalidOperationException($"Cannot set {Path}: {member.Name} is null.");
        Members[^1].SetValue(target, Value);
    }

    internal static IReadOnlyList<PropertyInfo> MembersOf(LambdaExpression member)
    {
        var path = new List<PropertyInfo>();
        var node = member.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert ? convert.Operand : member.Body;
        while (node is MemberExpression { Member: PropertyInfo property } access)
        {
            path.Insert(0, property);
            node = access.Expression;
        }
        if (node != member.Parameters[0] || path.Count == 0 || !path[^1].CanWrite)
            throw new ArgumentException($"'{member}' is not a settable property path of the row.", nameof(member));
        return path;
    }
}
