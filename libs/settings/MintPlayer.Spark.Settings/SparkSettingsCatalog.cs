using System.Reflection;

namespace MintPlayer.Spark.Settings;

/// <summary>
/// Every setting the application declared through <c>spark.AddSettings(…)</c>, by key (case-insensitive,
/// as configuration keys are). Filled while the services are registered, read-only afterwards.
/// </summary>
public sealed class SparkSettingsCatalog
{
    private readonly Dictionary<string, ISparkSetting> byKey = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The declared settings, in declaration order.</summary>
    public IReadOnlyCollection<ISparkSetting> All => byKey.Values;

    /// <summary>The declaration of <paramref name="key"/>, or null when no code declares it.</summary>
    public ISparkSetting? Find(string? key) => key is not null && byKey.TryGetValue(key, out var setting) ? setting : null;

    /// <summary>
    /// Adds every public static field or property of <paramref name="declaringType"/> that holds a
    /// <see cref="SparkSetting{T}"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">A key is malformed, or declared twice with different declarations.</exception>
    internal void AddFrom(Type declaringType)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
        var values = declaringType.GetFields(flags).Where(f => typeof(ISparkSetting).IsAssignableFrom(f.FieldType)).Select(f => f.GetValue(null))
            .Concat(declaringType.GetProperties(flags).Where(p => typeof(ISparkSetting).IsAssignableFrom(p.PropertyType) && p.GetIndexParameters().Length == 0).Select(p => p.GetValue(null)))
            .OfType<ISparkSetting>()
            .ToList();
        if (values.Count == 0)
            throw new InvalidOperationException($"'{declaringType.FullName}' declares no public static SparkSetting<T>, so AddSettings has nothing to add from it.");

        foreach (var setting in values)
            Add(setting);
    }

    internal void Add(ISparkSetting setting)
    {
        if (!SparkSettingValues.IsValidKey(setting.Key))
            throw new InvalidOperationException(
                $"The setting key '{setting.Key}' is not allowed. A key starts with a letter or digit and holds only letters, digits, ':', '.', '_' and '-' (it becomes the document id Settings/{{Key}}).");

        if (byKey.TryGetValue(setting.Key, out var existing))
        {
            if (Equals(existing, setting))
                return;
            throw new InvalidOperationException($"The setting '{setting.Key}' is declared twice, differently ('{existing.Key}' {existing.ValueType.Name} and {setting.ValueType.Name}).");
        }

        byKey.Add(setting.Key, setting);
    }
}
