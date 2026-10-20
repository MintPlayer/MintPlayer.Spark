using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// An amount of something in one unit (warehouse WX8): stock on hand, a pick quantity, a pack size, a weight.
/// Built on the same value-type infrastructure as <see cref="Money"/> (commerce X4).
/// <para>
/// <b>Units never mix.</b> The unit is a short code, upper-case (<c>EA</c>, <c>CS</c>, <c>PAL</c>,
/// <c>KG</c>, or a UN/ECE Recommendation 20 code such as <c>C62</c>). Adding, subtracting or comparing
/// (<c>&lt;</c>, <c>&gt;</c>) two quantities in different units throws <see cref="UnitMismatchException"/>:
/// 2 CS and 3 EA only add up through an item's <see cref="PackagingLevels"/>, which knows how many each a
/// case holds. <see cref="CompareTo(Quantity)"/> is the one exception: it is the total order a grid sorts by
/// (unit, then amount), not a statement that one unit is bigger than another.
/// </para>
/// <para>
/// <b>Exact.</b> The amount is a <see cref="decimal"/> and nothing rounds: <c>+</c>, <c>-</c> and
/// <c>× factor</c> are exact, and a packaging conversion that would not be exact is refused
/// (<see cref="PackagingLevels.Convert"/>). Trailing zeros carry no meaning (12.0 EA is 12 EA) and are
/// dropped, so the stored value is the same whichever way it was typed.
/// </para>
/// <para>
/// <b>One shape, everywhere</b>, as for <see cref="Money"/>: the API (<see cref="QuantityJsonConverter"/>),
/// RavenDB (<c>QuantityNewtonsoftJsonConverter</c> in MintPlayer.Spark) and the client all use
/// <code>{"Amount":12,"Unit":"EA"}</code>
/// with the CLR member names, so <c>x.OnHand.Amount</c> in a query or an index map addresses the stored field.
/// Reading is case-insensitive and also accepts the amount as a string (<c>"12.5"</c>, invariant).
/// </para>
/// </summary>
[JsonConverter(typeof(QuantityJsonConverter))]
public sealed class Quantity : IEquatable<Quantity>, IComparable<Quantity>, IComparable
{
    /// <summary>The longest unit code a <see cref="Quantity"/> accepts.</summary>
    public const int MaxUnitLength = 16;

    /// <summary>An amount in a unit.</summary>
    /// <param name="amount">The amount, in <paramref name="unit"/>. Negative amounts are allowed (a stock correction).</param>
    /// <param name="unit">A unit code: letters, digits, <c>_</c> or <c>-</c>, at most <see cref="MaxUnitLength"/>
    /// characters, case-insensitive (stored upper-case).</param>
    /// <exception cref="ArgumentException">The unit is not a valid unit code.</exception>
    public Quantity(decimal amount, string unit)
    {
        Unit = NormalizeUnit(unit);
        Amount = Normalize(amount);
    }

    /// <summary>The amount, without trailing zeros (<c>12</c>, <c>0.25</c>).</summary>
    public decimal Amount { get; }

    /// <summary>The unit code, upper-case.</summary>
    public string Unit { get; }

    /// <summary>Zero in <paramref name="unit"/>.</summary>
    public static Quantity Zero(string unit) => new(0m, unit);

    /// <summary>Whether the amount is zero.</summary>
    public bool IsZero => Amount == 0m;

    /// <summary>Whether <paramref name="unit"/> is a unit code a <see cref="Quantity"/> can hold (case-insensitive).</summary>
    public static bool IsValidUnit(string? unit) => unit is not null && TryNormalizeUnit(unit, out _);

    /// <summary>The same amount in a unit code normalized the way a <see cref="Quantity"/> stores it.</summary>
    /// <exception cref="ArgumentException">Not a valid unit code.</exception>
    public static string NormalizeUnit(string unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return TryNormalizeUnit(unit, out var code)
            ? code
            : throw new ArgumentException(
                $"'{unit}' is not a unit code: use 1 to {MaxUnitLength} letters, digits, '_' or '-' (EA, CS, KG, C62).", nameof(unit));
    }

    /// <summary>The sum of <paramref name="values"/>, all in <paramref name="unit"/>; zero when there are none.</summary>
    /// <exception cref="UnitMismatchException">A value is in another unit.</exception>
    public static Quantity Sum(IEnumerable<Quantity> values, string unit)
    {
        ArgumentNullException.ThrowIfNull(values);
        var total = Zero(unit);
        foreach (var value in values) total += value;
        return total;
    }

    // --- arithmetic: exact, single-unit ----------------------------------------------------------------

    public static Quantity operator +(Quantity left, Quantity right)
    {
        RequireSameUnit(left, right, "add");
        return new(checked(left.Amount + right.Amount), left.Unit);
    }

    public static Quantity operator -(Quantity left, Quantity right)
    {
        RequireSameUnit(left, right, "subtract");
        return new(checked(left.Amount - right.Amount), left.Unit);
    }

    public static Quantity operator -(Quantity value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(-value.Amount, value.Unit);
    }

    /// <summary>A quantity times a factor, in the same unit: 3 × (12 EA) is 36 EA. Exact.</summary>
    public static Quantity operator *(Quantity value, decimal factor)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(checked(value.Amount * factor), value.Unit);
    }

    /// <summary>A factor times a quantity, in the same unit. Exact.</summary>
    public static Quantity operator *(decimal factor, Quantity value) => value * factor;

    public static bool operator <(Quantity left, Quantity right) => CompareSameUnit(left, right) < 0;
    public static bool operator >(Quantity left, Quantity right) => CompareSameUnit(left, right) > 0;
    public static bool operator <=(Quantity left, Quantity right) => CompareSameUnit(left, right) <= 0;
    public static bool operator >=(Quantity left, Quantity right) => CompareSameUnit(left, right) >= 0;

    public static bool operator ==(Quantity? left, Quantity? right) => left is null ? right is null : left.Equals(right);
    public static bool operator !=(Quantity? left, Quantity? right) => !(left == right);

    // --- equality and ordering -------------------------------------------------------------------------

    /// <summary>
    /// Same unit and same amount. 1 CS and 12 EA are <b>not</b> equal here, whatever a case holds: that
    /// is a question for the item's <see cref="PackagingLevels.AreEqual"/>.
    /// </summary>
    public bool Equals(Quantity? other) => other is not null && Amount == other.Amount && Unit == other.Unit;

    public override bool Equals(object? obj) => obj is Quantity other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Amount, Unit);

    /// <summary>
    /// The order a grid sorts by: unit code (ordinal) first, then amount. A <see langword="null"/> sorts
    /// before any value. Never throws on mixed units; the comparison operators do.
    /// </summary>
    public int CompareTo(Quantity? other)
    {
        if (other is null) return 1;
        var byUnit = string.CompareOrdinal(Unit, other.Unit);
        return byUnit != 0 ? byUnit : Amount.CompareTo(other.Amount);
    }

    int IComparable.CompareTo(object? obj) => obj switch
    {
        null => 1,
        Quantity other => CompareTo(other),
        _ => throw new ArgumentException($"Cannot compare Quantity with {obj.GetType().Name}.", nameof(obj)),
    };

    /// <summary>Invariant, for logs and diagnostics: <c>12 EA</c>. Display formatting belongs to the client.</summary>
    public override string ToString() => $"{Amount.ToString(CultureInfo.InvariantCulture)} {Unit}";

    // --- helpers ---------------------------------------------------------------------------------------

    private static int CompareSameUnit(Quantity left, Quantity right)
    {
        RequireSameUnit(left, right, "compare");
        return left.Amount.CompareTo(right.Amount);
    }

    internal static void RequireSameUnit(Quantity left, Quantity right, string operation)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.Unit != right.Unit)
            throw new UnitMismatchException(left.Unit, right.Unit, operation);
    }

    private static bool TryNormalizeUnit(string unit, out string code)
    {
        code = unit.Trim().ToUpperInvariant();
        if (code.Length is 0 or > MaxUnitLength) return false;
        foreach (var c in code)
            if (c is not ((>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-'))
                return false;
        return true;
    }

    /// <summary>The same value without trailing zeros: 12.500 is 12.5, 12.0 is 12.</summary>
    internal static decimal Normalize(decimal amount) => amount / 1.0000000000000000000000000000m;
}

/// <summary>
/// Two quantities in different units met in an operation that has no single-unit answer: adding,
/// subtracting or comparing. Converting between units is never implicit; it goes through
/// <see cref="PackagingLevels"/>.
/// </summary>
public sealed class UnitMismatchException(string left, string right, string operation)
    : InvalidOperationException($"Cannot {operation} {left} and {right}: Quantity never mixes units. Convert through the item's packaging levels first.")
{
    /// <summary>The left operand's unit.</summary>
    public string LeftUnit { get; } = left;

    /// <summary>The right operand's unit.</summary>
    public string RightUnit { get; } = right;
}

/// <summary>
/// One packaging level of an item: how many base units one <paramref name="Unit"/> holds. A case of 12 is
/// <c>new PackagingLevel("CS", 12)</c>; a pallet of 40 cases is <c>new PackagingLevel("PAL", 480)</c>.
/// A level smaller than the base unit has a fraction (<c>new PackagingLevel("G", 0.001m)</c> under <c>KG</c>).
/// </summary>
/// <param name="Unit">The level's unit code.</param>
/// <param name="BaseQuantity">How many base units one of it holds; more than zero.</param>
public sealed record PackagingLevel(string Unit, decimal BaseQuantity);

/// <summary>
/// An item's packaging conversion table (warehouse WX8, PRD §4.5 "Units"): its base unit and the levels it
/// is handled in. Stock is stored in base units; levels convert at input (scanning a case barcode books its
/// pack quantity, <see cref="ToBase"/>) and at display (<see cref="Breakdown"/>: <c>2 CS + 3 EA</c>).
/// </summary>
/// <remarks>
/// Conversions are exact or refused, never rounded: 27 EA is 2.25 CS, but 1 EA in a case of 3 is not a
/// <see cref="decimal"/>-exact number of cases, so <see cref="Convert"/> throws
/// <see cref="QuantityConversionException"/> and <see cref="TryConvert"/> answers <see langword="false"/>.
/// </remarks>
public sealed class PackagingLevels
{
    private readonly Dictionary<string, decimal> factors = new(StringComparer.Ordinal);

    /// <summary>A table over <paramref name="baseUnit"/> and its <paramref name="levels"/>.</summary>
    /// <exception cref="ArgumentException">A unit code is invalid or listed twice, a level holds zero or fewer
    /// base units, or the base unit is listed with a quantity other than 1.</exception>
    public PackagingLevels(string baseUnit, IEnumerable<PackagingLevel>? levels = null)
    {
        BaseUnit = Quantity.NormalizeUnit(baseUnit);
        factors[BaseUnit] = 1m;

        foreach (var level in levels ?? [])
        {
            ArgumentNullException.ThrowIfNull(level, nameof(levels));
            var unit = Quantity.NormalizeUnit(level.Unit);
            if (level.BaseQuantity <= 0m)
                throw new ArgumentException($"Packaging level {unit} must hold more than zero {BaseUnit} (got {level.BaseQuantity.ToString(CultureInfo.InvariantCulture)}).", nameof(levels));
            if (unit == BaseUnit)
            {
                if (level.BaseQuantity != 1m)
                    throw new ArgumentException($"The base unit {BaseUnit} holds exactly 1 {BaseUnit}.", nameof(levels));
                continue;
            }
            if (!factors.TryAdd(unit, Quantity.Normalize(level.BaseQuantity)))
                throw new ArgumentException($"Packaging level {unit} is listed twice.", nameof(levels));
        }

        Levels = factors
            .Select(pair => new PackagingLevel(pair.Key, pair.Value))
            .OrderByDescending(level => level.BaseQuantity)
            .ThenBy(level => level.Unit, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>The unit stock is stored in.</summary>
    public string BaseUnit { get; }

    /// <summary>Every level, the base unit included, largest first.</summary>
    public IReadOnlyList<PackagingLevel> Levels { get; }

    /// <summary>Whether <paramref name="unit"/> is one of this table's levels (case-insensitive).</summary>
    public bool Knows(string? unit) => unit is not null && Quantity.IsValidUnit(unit) && factors.ContainsKey(Quantity.NormalizeUnit(unit));

    /// <summary>How many base units one <paramref name="unit"/> holds.</summary>
    /// <exception cref="QuantityConversionException">The unit is not a level of this table.</exception>
    public decimal BaseQuantityOf(string unit)
    {
        var code = Quantity.NormalizeUnit(unit);
        return factors.TryGetValue(code, out var factor)
            ? factor
            : throw new QuantityConversionException($"{code} is not a packaging level of this item (base unit {BaseUnit}).");
    }

    /// <summary>The quantity in base units: 2 CS in a case of 12 is 24 EA. Always exact.</summary>
    /// <exception cref="QuantityConversionException">The quantity's unit is not a level of this table.</exception>
    public Quantity ToBase(Quantity quantity)
    {
        ArgumentNullException.ThrowIfNull(quantity);
        return new(checked(quantity.Amount * BaseQuantityOf(quantity.Unit)), BaseUnit);
    }

    /// <summary>The quantity in <paramref name="unit"/>: 27 EA in a case of 12 is 2.25 CS.</summary>
    /// <exception cref="QuantityConversionException">A unit is not a level of this table, or the result is
    /// not exact in <see cref="decimal"/> (1 EA in a case of 3).</exception>
    public Quantity Convert(Quantity quantity, string unit)
        => TryConvert(quantity, unit, out var converted)
            ? converted
            : throw new QuantityConversionException(
                $"{quantity} is not an exact number of {Quantity.NormalizeUnit(unit)}: a conversion never rounds.");

    /// <summary><see cref="Convert"/> that answers <see langword="false"/> for an inexact result instead of throwing.</summary>
    /// <exception cref="QuantityConversionException">A unit is not a level of this table.</exception>
    public bool TryConvert(Quantity quantity, string unit, out Quantity converted)
    {
        var target = BaseQuantityOf(unit);
        var inBase = ToBase(quantity).Amount;
        var amount = inBase / target;
        converted = new Quantity(amount, unit);
        return amount * target == inBase;
    }

    /// <summary>
    /// The quantity as whole packages, largest level first, with what is left in the base unit: 27 EA in a
    /// case of 12 is <c>[2 CS, 3 EA]</c>. Levels with none are left out; zero is <c>[0 EA]</c>. A negative
    /// quantity breaks down the same way, every part negative. The parts always sum back to the input
    /// (<see cref="ToBase"/>).
    /// </summary>
    public IReadOnlyList<Quantity> Breakdown(Quantity quantity)
    {
        var remaining = ToBase(quantity).Amount;
        var negative = remaining < 0;
        remaining = Math.Abs(remaining);

        var parts = new List<Quantity>();
        foreach (var level in Levels)
        {
            if (level.Unit == BaseUnit) continue;
            var count = decimal.Truncate(remaining / level.BaseQuantity);
            if (count * level.BaseQuantity > remaining) count--;
            if (count <= 0) continue;
            parts.Add(new Quantity(negative ? -count : count, level.Unit));
            remaining -= count * level.BaseQuantity;
        }

        if (remaining != 0 || parts.Count == 0)
            parts.Add(new Quantity(negative ? -remaining : remaining, BaseUnit));
        return parts;
    }

    /// <summary>Compares two quantities through their base amounts: 1 CS against 12 EA is 0.</summary>
    public int Compare(Quantity left, Quantity right) => ToBase(left).Amount.CompareTo(ToBase(right).Amount);

    /// <summary>Whether two quantities are the same amount of the item: 1 CS and 12 EA in a case of 12.</summary>
    public bool AreEqual(Quantity left, Quantity right) => Compare(left, right) == 0;

    /// <summary>The sum of quantities in any of this table's levels, in base units.</summary>
    public Quantity Sum(IEnumerable<Quantity> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Quantity.Sum(values.Select(ToBase), BaseUnit);
    }
}

/// <summary>A packaging conversion that has no exact answer, or names a unit the item does not have.</summary>
public sealed class QuantityConversionException(string message) : InvalidOperationException(message);

/// <summary>
/// System.Text.Json converter for <see cref="Quantity"/>; the shape is documented on the type.
/// <c>QuantityNewtonsoftJsonConverter</c> (MintPlayer.Spark) writes and reads the same shape.
/// </summary>
public sealed class QuantityJsonConverter : JsonConverter<Quantity>
{
    /// <summary>The stored member names. CLR names on purpose; see <see cref="Quantity"/>.</summary>
    public const string AmountName = nameof(Quantity.Amount);

    /// <inheritdoc cref="AmountName"/>
    public const string UnitName = nameof(Quantity.Unit);

    public override Quantity? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Quantity must be an object: {\"Amount\":12,\"Unit\":\"EA\"}.");

        decimal? amount = null;
        string? unit = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString();
            reader.Read();
            if (string.Equals(name, AmountName, StringComparison.OrdinalIgnoreCase))
            {
                amount = reader.TokenType switch
                {
                    JsonTokenType.Number => reader.GetDecimal(),
                    JsonTokenType.String when decimal.TryParse(reader.GetString(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out var parsed) => parsed,
                    _ => throw new JsonException($"Quantity.Amount must be a number, not '{reader.TokenType}'."),
                };
            }
            else if (string.Equals(name, UnitName, StringComparison.OrdinalIgnoreCase))
            {
                unit = reader.TokenType == JsonTokenType.String
                    ? reader.GetString()
                    : throw new JsonException("Quantity.Unit must be a string.");
            }
            else
            {
                reader.Skip();
            }
        }

        if (amount is null || string.IsNullOrWhiteSpace(unit))
            throw new JsonException("Quantity needs both an Amount and a Unit.");

        try { return new Quantity(amount.Value, unit); }
        catch (ArgumentException ex) { throw new JsonException(ex.Message, ex); }
    }

    public override void Write(Utf8JsonWriter writer, Quantity value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber(AmountName, value.Amount);
        writer.WriteString(UnitName, value.Unit);
        writer.WriteEndObject();
    }
}
