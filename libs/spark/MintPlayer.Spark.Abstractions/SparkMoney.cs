namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// The model and index vocabulary of a <see cref="Money"/> attribute (commerce X4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Index companions.</b> A <see cref="Money"/> is stored as an object, <c>{"Amount":…,"Currency":…}</c>.
/// A collection query sorts and filters it by its members (<c>Price.Currency</c>, <c>Price.Amount</c>) and
/// RavenDB's auto-index covers them. A <b>static</b> index cannot be addressed by a nested path, so an index
/// that serves a <see cref="Money"/> column emits two flat fields, and its projection declares them
/// <c>[IgnoreProperty]</c> (they are index fields, not model attributes, exactly like a <c>{Name}Sort</c>
/// companion):
/// <code>
/// Map = products =&gt; from p in products select new
/// {
///     p.Price,
///     PriceAmount = p.Price.Amount,      // SparkMoney.AmountCompanion("Price")
///     PriceCurrency = p.Price.Currency,  // SparkMoney.CurrencyCompanion("Price")
/// };
/// </code>
/// The query executor sorts and filters through the companions whenever the projection declares both.
/// Without them a static index does not offer the column for sorting or filtering.
/// </para>
/// </remarks>
public static class SparkMoney
{
    /// <summary>The model <c>dataType</c> of a <see cref="Money"/> property.</summary>
    public const string DataType = "Money";

    /// <summary>
    /// The renderer model sync writes on a <see cref="DataType"/> attribute that has none: ng-spark's core
    /// <c>money</c> renderer (display with <c>Intl.NumberFormat</c>, amount + currency editor). Once
    /// written, the model file owns it, so an app can name its own renderer instead.
    /// </summary>
    public const string RendererName = "money";

    /// <summary>The index field holding a money property's amount: <c>{property}Amount</c>.</summary>
    public static string AmountCompanion(string property) => property + nameof(Money.Amount);

    /// <summary>The index field holding a money property's currency code: <c>{property}Currency</c>.</summary>
    public static string CurrencyCompanion(string property) => property + nameof(Money.Currency);
}
