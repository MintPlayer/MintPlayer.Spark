using MintPlayer.SourceGenerators.Attributes;

namespace MintPlayer.Spark.Helpers;

public interface ICollectionHelper
{
    string GetCollectionName(string clrType);
}

[Register(typeof(ICollectionHelper), ServiceLifetime.Singleton, "AddSparkServices")]
internal partial class CollectionHelper : ICollectionHelper
{
    public string GetCollectionName(string clrType)
    {
        var className = clrType.Split('.').Last();
        return Pluralize(className);
    }

    private static string Pluralize(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        if (name.EndsWith("y", StringComparison.OrdinalIgnoreCase) &&
            name.Length > 1 &&
            !IsVowel(name[^2]))
        {
            return name[..^1] + "ies";
        }
        if (name.EndsWith("s", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("x", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("ch", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("sh", StringComparison.OrdinalIgnoreCase))
        {
            return name + "es";
        }
        return name + "s";
    }

    private static bool IsVowel(char c) => "aeiouAEIOU".Contains(c);
}
