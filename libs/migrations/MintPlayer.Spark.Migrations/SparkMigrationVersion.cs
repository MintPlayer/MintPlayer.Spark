using System.Globalization;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.Migrations;

/// <summary>
/// A migration's version, read from its class name: <c>M_{yyyyMMddHHmm}_{Name}</c>, e.g.
/// <c>M_202610191200_TranslatedStringEntries</c> → <c>202610191200</c> (X20 item 9).
/// </summary>
/// <remarks>
/// The name and a hand-written <c>Version</c> used to say the same thing twice, and nothing checked that
/// they agreed: a copied class kept its old number and collided, or ran in the wrong order. Deriving the
/// version leaves one place to get it right, and the guard refuses a name that does not carry one, at
/// registration — that is, at startup of every application that references the migration.
/// </remarks>
public static partial class SparkMigrationVersion
{
    /// <summary>The version <typeparamref name="TMigration"/>'s class name carries.</summary>
    /// <exception cref="InvalidOperationException">The class name is not <c>M_{yyyyMMddHHmm}_{Name}</c>.</exception>
    public static long Of<TMigration>() where TMigration : ISparkMigration => Of(typeof(TMigration));

    /// <summary>The version <paramref name="migrationType"/>'s class name carries.</summary>
    /// <exception cref="InvalidOperationException">The class name is not <c>M_{yyyyMMddHHmm}_{Name}</c>.</exception>
    public static long Of(Type migrationType)
    {
        ArgumentNullException.ThrowIfNull(migrationType);

        if (TryParse(migrationType.Name, out var version))
            return version;

        throw new InvalidOperationException(
            $"Migration class '{migrationType.FullName}' is not named M_{{yyyyMMddHHmm}}_{{Name}}. A migration's version " +
            "is read from its class name (for example M_202610191200_BackfillOrderTotals runs as version 202610191200); " +
            "rename the class to its UTC creation minute and a short description.");
    }

    /// <summary>
    /// Reads the version from a class name, or returns <see langword="false"/> when the name is not
    /// <c>M_{yyyyMMddHHmm}_{Name}</c> with a real date and time.
    /// </summary>
    public static bool TryParse(string className, out long version)
    {
        version = 0;
        if (string.IsNullOrEmpty(className))
            return false;

        var match = NamePattern().Match(className);
        if (!match.Success)
            return false;

        var stamp = match.Groups["stamp"].Value;
        if (!DateTime.TryParseExact(stamp, "yyyyMMddHHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return false;

        version = long.Parse(stamp, NumberStyles.None, CultureInfo.InvariantCulture);
        return true;
    }

    [GeneratedRegex(@"^M_(?<stamp>[0-9]{12})_[A-Za-z0-9][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
