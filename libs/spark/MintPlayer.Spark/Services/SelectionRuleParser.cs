using System.Collections.Concurrent;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Parses a custom action's <c>selectionRule</c> — a cardinality expression over the number of
/// selected rows — into a predicate.
/// </summary>
/// <remarks>
/// <para>
/// The grammar is Vidyano's, deliberately, so a rule written by someone who knows Vidyano means
/// what they expect: <c>X</c> is the count placeholder, whitespace is insignificant, terms split
/// on <c>X</c> are AND-combined (so <c>1&lt;X&lt;5</c> is a range), the operators are
/// <c>&lt;=</c> <c>&gt;=</c> <c>&lt;</c> <c>&gt;</c> <c>!=</c> <c>=</c> — matched in that order,
/// so <c>&gt;=</c> is never read as <c>&gt;</c> — and a number-first term is mirrored, so
/// <c>0&lt;X</c> means <c>&gt;0</c>.
/// </para>
/// <para>
/// <b>Two deliberate departures from Vidyano.</b>
/// </para>
/// <para>
/// 1. <b>Malformed input throws instead of permitting everything.</b> Vidyano falls back to
/// "always true" on anything it cannot parse, which is defensible for a client-side hint that
/// only greys out a button. Spark enforces this rule on the server, where the same fallback
/// would mean a typo such as <c>"1-5"</c> silently permits any selection at all. Callers parse
/// at configuration-load time so a bad rule is a startup error, not a surprise at execute time.
/// </para>
/// <para>
/// 2. <b>The cache is concurrent.</b> Vidyano mutates a plain dictionary from multiple threads.
/// </para>
/// <para>
/// ⚠️ <b>This is not an authorization boundary.</b> It is input validation and a UX affordance.
/// The gate is the action's grant, enforced in <c>ExecuteCustomAction</c> regardless of which
/// query the caller clicked from — a caller can always POST directly. Enforcing cardinality
/// server-side buys integrity (an action written for one row never silently receives fifty) and
/// bounds the work a request can ask for; it buys no access control.
/// </para>
/// </remarks>
public static class SelectionRuleParser
{
    private static readonly ConcurrentDictionary<string, Func<int, bool>> cache = new();

    /// <summary>Matched longest-first, so <c>&gt;=</c> never parses as <c>&gt;</c>.</summary>
    private static readonly string[] operators = ["<=", ">=", "<", ">", "!=", "="];

    /// <summary>
    /// A predicate over the selected-row count. An absent or empty rule imposes no requirement.
    /// </summary>
    /// <remarks>
    /// "No rule means no requirement" resolves a contradiction in Spark's own docs: the guide
    /// said exactly this, while the older PRD claimed an omitted rule defaults to <c>"=0"</c>.
    /// The guide wins — it matches Vidyano, and it is the only reading that does not break every
    /// action that already omits the field.
    /// </remarks>
    public static Func<int, bool> Parse(string? rule)
    {
        if (string.IsNullOrWhiteSpace(rule)) return _ => true;
        return cache.GetOrAdd(rule, Compile);
    }

    /// <summary>
    /// Whether <paramref name="rule"/> is well-formed. Call at configuration load so a typo
    /// fails loudly at startup rather than permitting or refusing silently later.
    /// </summary>
    public static bool IsValid(string? rule)
    {
        if (string.IsNullOrWhiteSpace(rule)) return true;
        try { Compile(rule); return true; }
        catch (FormatException) { return false; }
    }

    private static Func<int, bool> Compile(string rule)
    {
        var normalized = rule.Replace(" ", string.Empty).ToUpperInvariant();
        var terms = normalized.Split('X', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) throw new FormatException($"Selection rule '{rule}' has no condition.");

        var predicates = new List<Func<int, bool>>(terms.Length);
        var numberFirst = !normalized.StartsWith('X') && normalized.Contains('X');

        for (var i = 0; i < terms.Length; i++)
        {
            // "0<X<5" splits into "0<" and "<5": the first term reads right-to-left and must be
            // mirrored, every later term reads normally.
            predicates.Add(CompileTerm(terms[i], rule, mirrored: numberFirst && i == 0));
        }

        return count => predicates.All(p => p(count));
    }

    private static Func<int, bool> CompileTerm(string term, string rule, bool mirrored)
    {
        var op = operators.FirstOrDefault(o => mirrored ? term.EndsWith(o, StringComparison.Ordinal)
                                                        : term.StartsWith(o, StringComparison.Ordinal))
            ?? throw new FormatException($"Selection rule '{rule}' has no recognised operator in '{term}'.");

        var numberPart = mirrored ? term[..^op.Length] : term[op.Length..];
        if (!int.TryParse(numberPart, out var value))
            throw new FormatException($"Selection rule '{rule}' has a non-numeric operand in '{term}'.");

        // Mirroring flips the comparison: "0<X" is "X>0".
        var effective = mirrored ? Mirror(op) : op;
        return effective switch
        {
            "<=" => count => count <= value,
            ">=" => count => count >= value,
            "<" => count => count < value,
            ">" => count => count > value,
            "!=" => count => count != value,
            "=" => count => count == value,
            _ => throw new FormatException($"Selection rule '{rule}' has an unsupported operator '{effective}'."),
        };
    }

    private static string Mirror(string op) => op switch
    {
        "<" => ">",
        ">" => "<",
        "<=" => ">=",
        ">=" => "<=",
        _ => op,
    };
}
