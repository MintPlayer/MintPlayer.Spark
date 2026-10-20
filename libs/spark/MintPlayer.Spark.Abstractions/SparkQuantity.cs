namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// The model and index vocabulary of a <see cref="Quantity"/> attribute (warehouse WX8), the counterpart of
/// <see cref="SparkMoney"/>.
/// </summary>
/// <remarks>
/// <b>Index companions.</b> A <see cref="Quantity"/> is stored as an object, <c>{"Amount":…,"Unit":…}</c>, so
/// a static index that serves a quantity column emits two flat fields, declared <c>[IgnoreProperty]</c> on
/// its projection (the <c>[GenerateIndex]</c> generator emits them):
/// <code>
/// Map = stock =&gt; from s in stock select new
/// {
///     s.OnHand,
///     OnHandAmount = s.OnHand.Amount,  // SparkQuantity.AmountCompanion("OnHand")
///     OnHandUnit = s.OnHand.Unit,      // SparkQuantity.UnitCompanion("OnHand")
/// };
/// </code>
/// The query executor sorts (unit, then amount) and filters through the companions whenever the projection
/// declares both; a collection query uses the nested members its auto-index covers.
/// </remarks>
public static class SparkQuantity
{
    /// <summary>The model <c>dataType</c> of a <see cref="Quantity"/> property.</summary>
    public const string DataType = "Quantity";

    /// <summary>
    /// The renderer model sync writes on a <see cref="DataType"/> attribute that has none: ng-spark's core
    /// <c>quantity</c> renderer (amount + unit display, packaging breakdown, amount + unit editor). Once
    /// written, the model file owns it.
    /// </summary>
    public const string RendererName = "quantity";

    /// <summary>The index field holding a quantity property's amount: <c>{property}Amount</c>.</summary>
    public static string AmountCompanion(string property) => property + nameof(Quantity.Amount);

    /// <summary>The index field holding a quantity property's unit code: <c>{property}Unit</c>.</summary>
    public static string UnitCompanion(string property) => property + nameof(Quantity.Unit);
}
