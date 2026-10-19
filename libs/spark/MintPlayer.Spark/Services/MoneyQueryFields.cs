using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Reflection;
using System.Linq.Expressions;
using System.Reflection;

namespace MintPlayer.Spark.Services;

/// <summary>
/// How a query sorts and filters a <see cref="Money"/> column (commerce X4): by its currency and amount,
/// never by the stored object.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="Money"/> is stored as an object, and RavenDB cannot order by or compare an object. The
/// column is therefore addressed through two scalar paths, chosen per row shape:
/// <list type="bullet">
/// <item>the <see cref="SparkMoney.AmountCompanion"/> / <see cref="SparkMoney.CurrencyCompanion"/> fields,
/// when the projection declares both as <c>[IgnoreProperty]</c> companions — what a static index needs;</item>
/// <item>otherwise the members themselves, <c>x.Price.Currency</c> and <c>x.Price.Amount</c>, which a
/// collection query's auto-index covers.</item>
/// </list>
/// </para>
/// <para>
/// Sorting is by currency, then amount: the same total order <see cref="Money.CompareTo(Money)"/> gives an
/// in-memory sort, so a grid orders the same way whichever side sorts it. With one currency it is simply
/// "by amount".
/// </para>
/// <para>
/// ⚠️ An in-memory sequence (a custom query that materialized its rows) needs a null guard on the nested
/// path, which RavenDB's LINQ provider cannot translate. Callers say which they hold.
/// </para>
/// </remarks>
internal static class MoneyQueryFields
{
    /// <summary>The two scalar paths of a money column, and the guard the nested form needs in memory.</summary>
    internal readonly record struct Paths(Expression Currency, Expression Amount, Expression? HasValue);

    public static bool IsMoney(PropertyInfo property)
        => (Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType) == typeof(Money);

    /// <summary>The companion pair on <paramref name="rowType"/>, or null when it does not declare both.</summary>
    public static (PropertyInfo Amount, PropertyInfo Currency)? Companions(Type rowType, string propertyName)
    {
        var amount = rowType.GetCachedProperty(SparkMoney.AmountCompanion(propertyName));
        var currency = rowType.GetCachedProperty(SparkMoney.CurrencyCompanion(propertyName));
        return amount is not null && currency is not null
            && amount.IsIgnoredForSparkModel() && currency.IsIgnoredForSparkModel()
            && (Nullable.GetUnderlyingType(amount.PropertyType) ?? amount.PropertyType) == typeof(decimal)
            && currency.PropertyType == typeof(string)
            ? (amount, currency)
            : null;
    }

    public static Paths For(ParameterExpression row, PropertyInfo moneyProperty, bool inMemory)
    {
        if (Companions(row.Type, moneyProperty.Name) is { } pair)
            return new(Expression.Property(row, pair.Currency), Expression.Property(row, pair.Amount), null);

        var money = Expression.Property(row, moneyProperty);
        var currencyPath = Expression.Property(money, nameof(Money.Currency));
        var amountPath = Expression.Property(money, nameof(Money.Amount));
        if (!inMemory)
            return new(currencyPath, amountPath, null);

        // In memory a row without a value must sort (as null) and must not throw.
        var hasValue = Expression.NotEqual(money, Expression.Constant(null, typeof(Money)));
        return new(
            Expression.Condition(hasValue, currencyPath, Expression.Constant(null, typeof(string))),
            Expression.Condition(hasValue, Expression.Convert(amountPath, typeof(decimal?)), Expression.Constant(null, typeof(decimal?))),
            hasValue);
    }

    /// <summary>The sort keys of a money column, in order: currency, then amount.</summary>
    public static Expression[] SortKeys(ParameterExpression row, PropertyInfo moneyProperty, bool inMemory)
    {
        var paths = For(row, moneyProperty, inMemory);
        return [paths.Currency, paths.Amount];
    }

    /// <summary>
    /// "The column equals this value": currency and amount both match, or, for a <see langword="null"/>
    /// request, the column holds no value.
    /// </summary>
    public static Expression EqualsValue(ParameterExpression row, PropertyInfo moneyProperty, Money? value, bool inMemory)
    {
        var paths = For(row, moneyProperty, inMemory);

        if (value is null)
        {
            // No value: the companion currency is absent, or the stored object is.
            return Companions(row.Type, moneyProperty.Name) is not null
                ? Expression.Equal(paths.Currency, Expression.Constant(null, paths.Currency.Type))
                : Expression.Equal(Expression.Property(row, moneyProperty), Expression.Constant(null, typeof(Money)));
        }

        var match = Expression.AndAlso(
            Expression.Equal(paths.Currency, Expression.Constant(value.Currency, paths.Currency.Type)),
            Expression.Equal(paths.Amount, Expression.Constant(value.Amount, paths.Amount.Type)));

        return paths.HasValue is null ? match : Expression.AndAlso(paths.HasValue, match);
    }

    /// <summary>
    /// Whether a row shape can sort and filter a money column: through the companions, or through the
    /// nested members when no static index restricts the field set.
    /// </summary>
    public static bool IsBacked(Type rowType, PropertyInfo moneyProperty, IReadOnlySet<string>? indexedFields)
    {
        if (indexedFields is null) return true;

        return Companions(rowType, moneyProperty.Name) is { } pair
            && indexedFields.Contains(pair.Amount.Name)
            && indexedFields.Contains(pair.Currency.Name);
    }
}
