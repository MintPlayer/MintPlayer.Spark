using System.Text;

namespace MintPlayer.Spark.Abstractions.Model;

/// <summary>
/// How the gates name a layer (composition D7): a library by its alias (D16), the application as
/// <see cref="App"/>. A drift message that names the layer that moved says whether a library update
/// or an edit of the application's files changed what the gate covers.
/// </summary>
public static class SparkLayerProvenance
{
    /// <summary>The application's own files, in every kind.</summary>
    public const string App = "app";

    /// <summary>The alias of the library <paramref name="assemblyName"/> is, among <paramref name="libraries"/>; the assembly name when it is none of them.</summary>
    public static string AliasOf(IEnumerable<SparkLibrary> libraries, string assemblyName)
        => libraries.FirstOrDefault(l => string.Equals(l.AssemblyName, assemblyName, StringComparison.Ordinal))?.Alias ?? assemblyName;

    /// <summary>Alias → assembly name of every library that states a layer in <paramref name="layers"/>.</summary>
    internal static SortedDictionary<string, string> Libraries(IEnumerable<SparkLibrary> libraries, IEnumerable<IReadOnlyDictionary<string, string>> layers)
    {
        var used = new HashSet<string>(layers.SelectMany(l => l.Keys), StringComparer.Ordinal);
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var library in libraries)
        {
            if (used.Contains(library.Alias))
                result[library.Alias] = library.AssemblyName;
        }
        return result;
    }

    internal static string Sha256Hex(string text)
        => Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(new UTF8Encoding(false).GetBytes(text)));
}
