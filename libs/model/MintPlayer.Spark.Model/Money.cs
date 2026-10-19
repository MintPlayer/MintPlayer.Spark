using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// An amount of money in one ISO 4217 currency (commerce X4): prices, fees, discounts, refunds.
/// <para>
/// <b>Exact by construction.</b> The amount is held as whole minor units of its currency (cents for
/// EUR, yen for JPY, fils for KWD; <see cref="MinorUnitDigits"/>), so a <see cref="Money"/> can never
/// hold 12.345 EUR. The constructor <b>refuses</b> such an amount rather than rounding it: rounding is a
/// decision, and it is made in exactly two places, <see cref="RoundDiv(long, long)"/> (one quotient, half away from
/// zero, once) and <see cref="Allocate(long, IReadOnlyList{long})"/> (a split that sums exactly, largest
/// remainder). Every other operation (<c>+</c>, <c>-</c>, <c>× quantity</c>) is exact. These rules were
/// measured against 10,000 generated orders × 4 VAT configurations in spike S21 (commerce PRD §7), with
/// totals, VAT bases and credit notes reconciling to the cent.
/// </para>
/// <para>
/// <b>Currencies never mix.</b> Adding, subtracting or comparing (<c>&lt;</c>, <c>&gt;</c>) two amounts
/// in different currencies throws <see cref="CurrencyMismatchException"/>. <see cref="CompareTo(Money)"/>
/// is the one exception: it is the total order a grid sorts by (currency, then amount), not a statement
/// that one currency is worth more than another.
/// </para>
/// <para>
/// <b>One shape, everywhere</b>: the API (System.Text.Json, <see cref="MoneyJsonConverter"/>), RavenDB
/// (Newtonsoft, through <c>MoneyNewtonsoftJsonConverter</c> in MintPlayer.Spark) and the client all use
/// <code>{"Amount":12.30,"Currency":"EUR"}</code>
/// Member names are the CLR names whatever the serializer's naming policy, as for
/// <see cref="TranslatedString"/>: RavenDB's LINQ translator addresses stored JSON by CLR member name, so
/// <c>x.Price.Amount</c> in a query or an index map only works when the stored names are exactly these.
/// Reading is case-insensitive and also accepts the amount as a string (<c>"12.30"</c>, invariant).
/// </para>
/// </summary>
[JsonConverter(typeof(MoneyJsonConverter))]
public sealed class Money : IEquatable<Money>, IComparable<Money>, IComparable
{
    private readonly long minorUnits;

    /// <summary>
    /// An amount in a currency.
    /// </summary>
    /// <param name="amount">The amount in major units (<c>12.30m</c> for €12.30). It may not carry more
    /// decimals than the currency has minor-unit digits; trailing zeros are fine.</param>
    /// <param name="currency">An ISO 4217 alphabetic code, case-insensitive (stored upper-case).</param>
    /// <exception cref="ArgumentException">The currency is not an ISO 4217 code with minor units, or the
    /// amount is more precise than the currency allows. Round it first, with <see cref="RoundDiv(long, long)"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The amount does not fit in a 64-bit count of minor units.</exception>
    public Money(decimal amount, string currency)
    {
        Currency = NormalizeCurrency(currency);
        var digits = Iso4217.Digits[Currency];
        decimal scaled;
        try { scaled = amount * Pow10[digits]; }
        catch (OverflowException) { throw new ArgumentOutOfRangeException(nameof(amount), amount, "The amount does not fit in a 64-bit count of minor units."); }
        if (decimal.Truncate(scaled) != scaled)
            throw new ArgumentException(
                $"{amount.ToString(CultureInfo.InvariantCulture)} {Currency} has more decimals than the currency's " +
                $"{digits} minor-unit digit(s). Money never rounds silently: round once with Money.RoundDiv first.",
                nameof(amount));
        if (scaled < long.MinValue || scaled > long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "The amount does not fit in a 64-bit count of minor units.");

        minorUnits = (long)scaled;
        Amount = ToDecimal(minorUnits, digits);
    }

    private Money(long minorUnits, string normalizedCurrency, int digits)
    {
        this.minorUnits = minorUnits;
        Currency = normalizedCurrency;
        Amount = ToDecimal(minorUnits, digits);
    }

    /// <summary>The amount in major units, carrying exactly the currency's number of decimals (<c>12.30</c>, never <c>12.3</c>).</summary>
    public decimal Amount { get; }

    /// <summary>The ISO 4217 alphabetic code, upper-case.</summary>
    public string Currency { get; }

    /// <summary>The amount as a whole number of minor units: 1230 for €12.30, 1230 for ¥1230.</summary>
    public long ToMinorUnits() => minorUnits;

    /// <summary>An amount given in minor units: <c>Money.FromMinorUnits(1230, "EUR")</c> is €12.30.</summary>
    public static Money FromMinorUnits(long minorUnits, string currency)
    {
        var code = NormalizeCurrency(currency);
        return new Money(minorUnits, code, Iso4217.Digits[code]);
    }

    /// <summary>Zero in <paramref name="currency"/>.</summary>
    public static Money Zero(string currency) => FromMinorUnits(0, currency);

    /// <summary>How many minor-unit digits an ISO 4217 currency has: 2 for EUR, 0 for JPY, 3 for KWD.</summary>
    /// <exception cref="ArgumentException">Not a known ISO 4217 currency with minor units.</exception>
    public static int MinorUnitDigits(string currency) => Iso4217.Digits[NormalizeCurrency(currency)];

    /// <summary>Whether <paramref name="currency"/> is an ISO 4217 code a <see cref="Money"/> can hold (case-insensitive).</summary>
    public static bool IsKnownCurrency(string? currency)
        => currency is not null && Iso4217.Digits.ContainsKey(currency.Trim().ToUpperInvariant());

    /// <summary>Every currency code a <see cref="Money"/> can hold, ordinal order.</summary>
    public static IReadOnlyList<string> KnownCurrencies { get; } = Iso4217.Digits.Keys.Order(StringComparer.Ordinal).ToArray();

    /// <summary>Whether the amount is zero.</summary>
    public bool IsZero => minorUnits == 0;

    // --- the two rounding primitives (S21 rule R-a, R-c) ---------------------------------------------

    /// <summary>
    /// <c>numerator / denominator</c> rounded to a whole number <b>half away from zero</b>: 2.5 → 3,
    /// -2.5 → -3, 2.49 → 2. The only rounding rule money uses, and it is applied once per quotient, never to
    /// an intermediate result.
    /// </summary>
    /// <example>A 21% VAT on a net of 1999 cents: <c>Money.RoundDiv(1999 * 21, 100)</c> = 420.</example>
    /// <exception cref="DivideByZeroException"><paramref name="denominator"/> is zero.</exception>
    /// <exception cref="OverflowException">The result does not fit a <see cref="long"/>.</exception>
    public static long RoundDiv(long numerator, long denominator)
        => checked((long)RoundDiv((Int128)numerator, denominator));

    /// <summary>
    /// <see cref="RoundDiv(long, long)"/> for a numerator that is already a product, so
    /// <c>amount × rate</c> cannot overflow before it is divided.
    /// </summary>
    public static Int128 RoundDiv(Int128 numerator, Int128 denominator)
    {
        if (denominator == 0) throw new DivideByZeroException();
        if (denominator < 0) { numerator = -numerator; denominator = -denominator; }

        var (quotient, remainder) = Int128.DivRem(Int128.Abs(numerator), denominator);
        // remainder >= denominator - remainder is 2r >= d without the overflow.
        if (remainder >= denominator - remainder) quotient++;
        return numerator < 0 ? -quotient : quotient;
    }

    /// <summary>
    /// Splits <paramref name="total"/> over <paramref name="weights"/> so the parts <b>sum exactly</b> to it,
    /// each within one unit of its exact pro-rata share (largest remainder).
    /// <para>
    /// Each part starts as the truncated share; the units left over go one each to the largest
    /// remainders. Ties go to the larger weight, then the earlier index, so the result is deterministic.
    /// A zero weight gets nothing. When <c>|total| ≤ Σweights</c>, no part exceeds its weight, which is what
    /// keeps a discount from making a line negative. A negative total is split as its absolute value and
    /// negated, so the split of a refund mirrors the split of the charge.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">A weight is negative, or <paramref name="total"/> is not zero and every weight is.</exception>
    public static long[] Allocate(long total, IReadOnlyList<long> weights)
    {
        ArgumentNullException.ThrowIfNull(weights);

        Int128 sum = 0;
        for (var i = 0; i < weights.Count; i++)
        {
            if (weights[i] < 0)
                throw new ArgumentException($"Weight {i} is negative ({weights[i]}); allocation weights must be zero or more.", nameof(weights));
            sum += weights[i];
        }

        var parts = new long[weights.Count];
        if (total == 0) return parts;
        if (sum == 0)
            throw new ArgumentException("Cannot allocate a non-zero total over weights that are all zero.", nameof(weights));

        var negative = total < 0;
        var absolute = negative ? -(Int128)total : total;

        var remainders = new (Int128 Remainder, long Weight, int Index)[weights.Count];
        Int128 given = 0;
        for (var i = 0; i < weights.Count; i++)
        {
            var (part, remainder) = Int128.DivRem(absolute * weights[i], sum);
            parts[i] = (long)part;
            given += part;
            remainders[i] = (remainder, weights[i], i);
        }

        var left = (long)(absolute - given);
        if (left > 0)
        {
            Array.Sort(remainders, static (a, b) =>
            {
                var byRemainder = b.Remainder.CompareTo(a.Remainder);
                if (byRemainder != 0) return byRemainder;
                var byWeight = b.Weight.CompareTo(a.Weight);
                return byWeight != 0 ? byWeight : a.Index.CompareTo(b.Index);
            });
            for (var k = 0; k < left; k++)
                parts[remainders[k].Index]++;
        }

        if (negative)
            for (var i = 0; i < parts.Length; i++) parts[i] = -parts[i];

        return parts;
    }

    // --- the same primitives on amounts ---------------------------------------------------------------

    /// <summary>
    /// <c>this × numerator / denominator</c>, rounded once (<see cref="RoundDiv(long, long)"/>). Percentages,
    /// VAT and pro-rata refunds are all this: 15% off is <c>price.Share(15, 100)</c>, 12.5% is
    /// <c>price.Share(1250, 10_000)</c>, the VAT inside a 21% gross price is <c>gross.Share(21, 121)</c>.
    /// </summary>
    public Money Share(long numerator, long denominator)
        => new(checked((long)RoundDiv((Int128)minorUnits * numerator, denominator)), Currency, Iso4217.Digits[Currency]);

    /// <summary>
    /// This amount split over <paramref name="weights"/>, summing exactly to it
    /// (<see cref="Allocate(long, IReadOnlyList{long})"/>): a discount over order lines, a refund over what
    /// each line can still refund.
    /// </summary>
    public Money[] Allocate(IReadOnlyList<long> weights)
    {
        var digits = Iso4217.Digits[Currency];
        return Array.ConvertAll(Allocate(minorUnits, weights), part => new Money(part, Currency, digits));
    }

    /// <summary>
    /// This amount split pro rata over other amounts in the same currency, summing exactly to it — the
    /// order discount over the line amounts it applies to.
    /// </summary>
    /// <exception cref="CurrencyMismatchException">A weight is in another currency.</exception>
    public Money[] Allocate(IReadOnlyList<Money> weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        var units = new long[weights.Count];
        for (var i = 0; i < weights.Count; i++)
        {
            RequireSameCurrency(this, weights[i], "allocate");
            units[i] = weights[i].minorUnits;
        }
        return Allocate(units);
    }

    /// <summary>The sum of <paramref name="values"/>, all in <paramref name="currency"/>; zero when there are none.</summary>
    /// <exception cref="CurrencyMismatchException">A value is in another currency.</exception>
    public static Money Sum(IEnumerable<Money> values, string currency)
    {
        ArgumentNullException.ThrowIfNull(values);
        var total = Zero(currency);
        foreach (var value in values) total += value;
        return total;
    }

    // --- arithmetic: exact, single-currency -----------------------------------------------------------

    public static Money operator +(Money left, Money right)
    {
        RequireSameCurrency(left, right, "add");
        return new(checked(left.minorUnits + right.minorUnits), left.Currency, Iso4217.Digits[left.Currency]);
    }

    public static Money operator -(Money left, Money right)
    {
        RequireSameCurrency(left, right, "subtract");
        return new(checked(left.minorUnits - right.minorUnits), left.Currency, Iso4217.Digits[left.Currency]);
    }

    public static Money operator -(Money value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(checked(-value.minorUnits), value.Currency, Iso4217.Digits[value.Currency]);
    }

    /// <summary>A unit price times a quantity: exact. There is deliberately no <c>× decimal</c>; use <see cref="Share"/>.</summary>
    public static Money operator *(Money value, long quantity)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(checked(value.minorUnits * quantity), value.Currency, Iso4217.Digits[value.Currency]);
    }

    /// <summary>A quantity times a unit price: exact.</summary>
    public static Money operator *(long quantity, Money value) => value * quantity;

    public static bool operator <(Money left, Money right) => CompareSameCurrency(left, right) < 0;
    public static bool operator >(Money left, Money right) => CompareSameCurrency(left, right) > 0;
    public static bool operator <=(Money left, Money right) => CompareSameCurrency(left, right) <= 0;
    public static bool operator >=(Money left, Money right) => CompareSameCurrency(left, right) >= 0;

    public static bool operator ==(Money? left, Money? right) => left is null ? right is null : left.Equals(right);
    public static bool operator !=(Money? left, Money? right) => !(left == right);

    // --- equality and ordering -------------------------------------------------------------------------

    /// <summary>Same currency and same amount; €12.3 and €12.30 are equal (they are the same value).</summary>
    public bool Equals(Money? other)
        => other is not null && minorUnits == other.minorUnits && Currency == other.Currency;

    public override bool Equals(object? obj) => obj is Money other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(minorUnits, Currency);

    /// <summary>
    /// The order a grid sorts by: currency code (ordinal) first, then amount. A <see langword="null"/>
    /// sorts before any value. This never throws on mixed currencies, because sorting a column that holds
    /// several must still produce a result; the comparison operators do throw.
    /// </summary>
    public int CompareTo(Money? other)
    {
        if (other is null) return 1;
        var byCurrency = string.CompareOrdinal(Currency, other.Currency);
        return byCurrency != 0 ? byCurrency : minorUnits.CompareTo(other.minorUnits);
    }

    int IComparable.CompareTo(object? obj) => obj switch
    {
        null => 1,
        Money other => CompareTo(other),
        _ => throw new ArgumentException($"Cannot compare Money with {obj.GetType().Name}.", nameof(obj)),
    };

    /// <summary>Invariant, for logs and diagnostics: <c>12.30 EUR</c>. Display formatting belongs to the client (<c>Intl.NumberFormat</c>).</summary>
    public override string ToString() => $"{Amount.ToString(CultureInfo.InvariantCulture)} {Currency}";

    // --- helpers ---------------------------------------------------------------------------------------

    private static int CompareSameCurrency(Money left, Money right)
    {
        RequireSameCurrency(left, right, "compare");
        return left.minorUnits.CompareTo(right.minorUnits);
    }

    private static void RequireSameCurrency(Money left, Money right, string operation)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.Currency != right.Currency)
            throw new CurrencyMismatchException(left.Currency, right.Currency, operation);
    }

    private static string NormalizeCurrency(string currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        var code = currency.Trim().ToUpperInvariant();
        if (!Iso4217.Digits.ContainsKey(code))
            throw new ArgumentException(
                $"'{currency}' is not an ISO 4217 currency code a Money can hold.", nameof(currency));
        return code;
    }

    private static readonly decimal[] Pow10 = [1m, 10m, 100m, 1000m, 10000m];

    /// <summary>A decimal with exactly <paramref name="scale"/> decimals: 1230 at scale 2 is 12.30.</summary>
    private static decimal ToDecimal(long minorUnits, int scale)
    {
        var magnitude = minorUnits < 0 ? (ulong)(-(minorUnits + 1)) + 1 : (ulong)minorUnits;
        return new decimal((int)(uint)magnitude, (int)(uint)(magnitude >> 32), 0, minorUnits < 0, (byte)scale);
    }
}

/// <summary>
/// Two amounts in different currencies met in an operation that has no single-currency answer:
/// adding, subtracting, comparing or allocating. Converting between currencies is never implicit.
/// </summary>
public sealed class CurrencyMismatchException(string left, string right, string operation)
    : InvalidOperationException($"Cannot {operation} {left} and {right}: Money never mixes currencies.")
{
    /// <summary>The left operand's currency.</summary>
    public string LeftCurrency { get; } = left;

    /// <summary>The right operand's currency.</summary>
    public string RightCurrency { get; } = right;
}

/// <summary>
/// System.Text.Json converter for <see cref="Money"/>; the shape is documented on the type.
/// <c>MoneyNewtonsoftJsonConverter</c> (MintPlayer.Spark) writes and reads the same shape, so the API
/// and RavenDB cannot drift apart.
/// </summary>
public sealed class MoneyJsonConverter : JsonConverter<Money>
{
    /// <summary>The stored member names. CLR names on purpose; see <see cref="Money"/>.</summary>
    public const string AmountName = nameof(Money.Amount);

    /// <inheritdoc cref="AmountName"/>
    public const string CurrencyName = nameof(Money.Currency);

    public override Money? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Money must be an object: {\"Amount\":12.30,\"Currency\":\"EUR\"}.");

        decimal? amount = null;
        string? currency = null;
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
                    _ => throw new JsonException($"Money.Amount must be a number, not '{reader.TokenType}'."),
                };
            }
            else if (string.Equals(name, CurrencyName, StringComparison.OrdinalIgnoreCase))
            {
                currency = reader.TokenType == JsonTokenType.String
                    ? reader.GetString()
                    : throw new JsonException("Money.Currency must be a string.");
            }
            else
            {
                reader.Skip();
            }
        }

        if (amount is null || string.IsNullOrWhiteSpace(currency))
            throw new JsonException("Money needs both an Amount and a Currency.");

        try { return new Money(amount.Value, currency); }
        catch (ArgumentException ex) { throw new JsonException(ex.Message, ex); }
    }

    public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber(AmountName, value.Amount);
        writer.WriteString(CurrencyName, value.Currency);
        writer.WriteEndObject();
    }
}

/// <summary>
/// The ISO 4217 "List One" currencies (active codes, 2025 edition) with their minor-unit digits.
/// Codes without minor units (precious metals, SDR, test and "no currency" codes: XAU, XAG, XPT, XPD,
/// XDR, XBA–XBD, XSU, XUA, XTS, XXX) are left out: they are not money a shop can charge.
/// </summary>
internal static class Iso4217
{
    private const string Zero = "BIF CLP DJF GNF ISK JPY KMF KRW PYG RWF UGX UYI VND VUV XAF XOF XPF";
    private const string Three = "BHD IQD JOD KWD LYD OMR TND";
    private const string Four = "CLF UYW";
    private const string Two =
        "AED AFN ALL AMD ANG AOA ARS AUD AWG AZN BAM BBD BDT BGN BMD BND BOB BOV BRL BSD BTN BWP BYN BZD " +
        "CAD CDF CHE CHF CHW CNY COP COU CRC CUP CVE CZK DKK DOP DZD EGP ERN ETB EUR FJD FKP GBP GEL GHS " +
        "GIP GMD GTQ GYD HKD HNL HTG HUF IDR ILS INR IRR JMD KES KGS KHR KPW KYD KZT LAK LBP LKR LRD LSL " +
        "MAD MDL MGA MKD MMK MNT MOP MRU MUR MVR MWK MXN MXV MYR MZN NAD NGN NIO NOK NPR NZD PAB PEN PGK " +
        "PHP PKR PLN QAR RON RSD RUB SAR SBD SCR SDG SEK SGD SHP SLE SOS SRD SSP STN SVC SYP SZL THB TJS " +
        "TMT TOP TRY TTD TWD TZS UAH USD USN UYU UZS VED VES WST XCD XCG YER ZAR ZMW ZWG";

    public static readonly FrozenDictionary<string, int> Digits = Build();

    private static FrozenDictionary<string, int> Build()
    {
        var digits = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (codes, count) in new[] { (Zero, 0), (Two, 2), (Three, 3), (Four, 4) })
            foreach (var code in codes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                digits.Add(code, count);
        return digits.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
