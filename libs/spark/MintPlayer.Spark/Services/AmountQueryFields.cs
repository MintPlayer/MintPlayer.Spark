using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Reflection;
using System.Linq.Expressions;
using System.Reflection;

namespace MintPlayer.Spark.Services;

/// <summary>
/// How a query sorts and filters a column holding an amount with a code: a <see cref="Money"/> (commerce
/// X4: amount + currency) or a <see cref="Quantity"/> (warehouse WX8: amount + unit). By the code and the
/// amount, never by the stored object.
/// </summary>
/// <remarks>
/// <para>
/// Such a value is stored as an object, and RavenDB cannot order by or compare an object. The column is
/// therefore addressed through two scalar paths, chosen per row shape:
/// <list type="bullet">
/// <item>the companion fields (<see cref="SparkMoney.AmountCompanion"/> / <see cref="SparkMoney.CurrencyCompanion"/>,
/// <see cref="SparkQuantity.AmountCompanion"/> / <see cref="SparkQuantity.UnitCompanion"/>), when the
/// projection declares both as <c>[IgnoreProperty]</c> companions — what a static index needs;</item>
/// <item>otherwise the members themselves, <c>x.Price.Currency</c> and <c>x.Price.Amount</c>, which a
/// collection query's auto-index covers.</item>
/// </list>
/// </para>
/// <para>
/// Sorting is by code, then amount: the same total order <see cref="Money.CompareTo(Money)"/> and
/// <see cref="Quantity.CompareTo(Quantity)"/> give an in-memory sort, so a grid orders the same way
/// whichever side sorts it. With one currency or unit it is simply "by amount".
/// </para>
/// <para>
/// ⚠️ An in-memory sequence (a custom query that materialized its rows) needs a null guard on the nested
/// path, which RavenDB's LINQ provider cannot translate. Callers say which they hold.
/// </para>
/// </remarks>
internal static class AmountQueryFields
{
    /// <summary>The two scalar paths of a column, and the guard the nested form needs in memory.</summary>
    internal readonly record struct Paths(Expression Code, Expression Amount, Expression? HasValue);

    /// <summary>One value type of this kind: its CLR type, its code member and its companion names.</summary>
    internal sealed record Kind(
        Type ValueType,
        string CodeMember,
        Func<string, string> AmountCompanion,
        Func<string, string> CodeCompanion,
        Func<object, (string Code, decimal Amount)> Read);

    private static readonly Kind MoneyKind = new(
        typeof(Money), nameof(Money.Currency), SparkMoney.AmountCompanion, SparkMoney.CurrencyCompanion,
        value => (((Money)value).Currency, ((Money)value).Amount));

    private static readonly Kind QuantityKind = new(
        typeof(Quantity), nameof(Quantity.Unit), SparkQuantity.AmountCompanion, SparkQuantity.UnitCompanion,
        value => (((Quantity)value).Unit, ((Quantity)value).Amount));

    /// <summary>The kind of <paramref name="property"/>'s type, or null when it is neither Money nor Quantity.</summary>
    public static Kind? KindOf(PropertyInfo property)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        return type == typeof(Money) ? MoneyKind
            : type == typeof(Quantity) ? QuantityKind
            : null;
    }

    /// <summary>Whether <paramref name="property"/> is a Money or a Quantity column.</summary>
    public static bool Handles(PropertyInfo property) => KindOf(property) is not null;

    /// <summary>The companion pair on <paramref name="rowType"/>, or null when it does not declare both.</summary>
    public static (PropertyInfo Amount, PropertyInfo Code)? Companions(Type rowType, PropertyInfo property)
    {
        if (KindOf(property) is not { } kind) return null;
        var amount = rowType.GetCachedProperty(kind.AmountCompanion(property.Name));
        var code = rowType.GetCachedProperty(kind.CodeCompanion(property.Name));
        return amount is not null && code is not null
            && amount.IsIgnoredForSparkModel() && code.IsIgnoredForSparkModel()
            && (Nullable.GetUnderlyingType(amount.PropertyType) ?? amount.PropertyType) == typeof(decimal)
            && code.PropertyType == typeof(string)
            ? (amount, code)
            : null;
    }

    public static Paths For(ParameterExpression row, PropertyInfo property, bool inMemory)
    {
        var kind = KindOf(property) ?? throw new ArgumentException($"{property.Name} is neither Money nor Quantity.", nameof(property));
        if (Companions(row.Type, property) is { } pair)
            return new(Expression.Property(row, pair.Code), Expression.Property(row, pair.Amount), null);

        var value = Expression.Property(row, property);
        var codePath = Expression.Property(value, kind.CodeMember);
        var amountPath = Expression.Property(value, nameof(Money.Amount));
        if (!inMemory)
            return new(codePath, amountPath, null);

        // In memory a row without a value must sort (as null) and must not throw.
        var hasValue = Expression.NotEqual(value, Expression.Constant(null, kind.ValueType));
        return new(
            Expression.Condition(hasValue, codePath, Expression.Constant(null, typeof(string))),
            Expression.Condition(hasValue, Expression.Convert(amountPath, typeof(decimal?)), Expression.Constant(null, typeof(decimal?))),
            hasValue);
    }

    /// <summary>The sort keys of the column, in order: code, then amount.</summary>
    public static Expression[] SortKeys(ParameterExpression row, PropertyInfo property, bool inMemory)
    {
        var paths = For(row, property, inMemory);
        return [paths.Code, paths.Amount];
    }

    /// <summary>
    /// "The column equals this value": code and amount both match, or, for a <see langword="null"/>
    /// request, the column holds no value.
    /// </summary>
    public static Expression EqualsValue(ParameterExpression row, PropertyInfo property, object? value, bool inMemory)
    {
        var kind = KindOf(property) ?? throw new ArgumentException($"{property.Name} is neither Money nor Quantity.", nameof(property));
        var paths = For(row, property, inMemory);

        if (value is null)
        {
            // No value: the companion code is absent, or the stored object is.
            return Companions(row.Type, property) is not null
                ? Expression.Equal(paths.Code, Expression.Constant(null, paths.Code.Type))
                : Expression.Equal(Expression.Property(row, property), Expression.Constant(null, kind.ValueType));
        }

        var (code, amount) = kind.Read(value);
        var match = Expression.AndAlso(
            Expression.Equal(paths.Code, Expression.Constant(code, paths.Code.Type)),
            Expression.Equal(paths.Amount, Expression.Constant(amount, paths.Amount.Type)));

        return paths.HasValue is null ? match : Expression.AndAlso(paths.HasValue, match);
    }

    /// <summary>
    /// Whether a row shape can sort and filter the column: through the companions, or through the
    /// nested members when no static index restricts the field set.
    /// </summary>
    public static bool IsBacked(Type rowType, PropertyInfo property, IReadOnlySet<string>? indexedFields)
    {
        if (indexedFields is null) return true;

        return Companions(rowType, property) is { } pair
            && indexedFields.Contains(pair.Amount.Name)
            && indexedFields.Contains(pair.Code.Name);
    }
}
