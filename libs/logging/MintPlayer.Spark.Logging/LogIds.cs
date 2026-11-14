using System.Text.RegularExpressions;

namespace MintPlayer.Spark.Logging;

/// <summary>The ids of <c>Log</c> documents: <c>Logs/{key}</c>, the key validated (#660 G10).</summary>
public static partial class LogIds
{
    /// <summary>The prefix every log id carries, so a key can never name another collection's document.</summary>
    public const string Prefix = "Logs/";

    /// <summary>The longest key accepted.</summary>
    public const int MaxKeyLength = 200;

    /// <summary>The id for <paramref name="key"/>.</summary>
    /// <exception cref="ArgumentException">The key is not a feature prefix and an id of the allowed characters.</exception>
    public static string ForKey(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength || !KeyPattern().IsMatch(key))
            throw new ArgumentException(
                $"A log key is a feature prefix and an id, such as 'commands/github/123/456': letters, digits, '-', '_', '.', ':' "
                + $"in segments separated by '/', at least two segments, at most {MaxKeyLength} characters.",
                nameof(key));
        return Prefix + key;
    }

    /// <summary>A new log's id.</summary>
    public static string New() => Prefix + Guid.NewGuid().ToString("N");

    // Segments of [A-Za-z0-9_.:-], none empty, none "." or ".." (no path tricks in a key that ends up in URLs).
    [GeneratedRegex(@"^(?!.*(^|/)\.{1,2}(/|$))[A-Za-z0-9_.:\-]+(/[A-Za-z0-9_.:\-]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
