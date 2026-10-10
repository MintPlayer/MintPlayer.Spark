using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Settings.Models;

namespace MintPlayer.Spark.Settings.Interceptors;

/// <summary>
/// Save-time validation of a <see cref="Setting"/> (<c>docs/seo_analyzer_PRD.md</c> X9) and the local
/// cache invalidation:
/// <list type="bullet">
/// <item>the key must be declared in code; an edit never changes the key or the code-owned columns;</item>
/// <item>an empty value means "not overridden" and is stored as null;</item>
/// <item>any other value must parse as the declared type, the way configuration would parse it;</item>
/// <item>after a committed save or delete the saving instance drops its snapshot, so the change applies there at once.</item>
/// </list>
/// </summary>
internal sealed class SettingInterceptors(SparkSettingsCatalog catalog, ISparkSettings settings)
    : IBeforeSave<Setting>, IAfterSave<Setting>, IAfterDelete<Setting>
{
    public ValueTask OnBeforeSaveAsync(Setting entity, SaveContext context)
    {
        if (context.Before is Setting stored)
        {
            entity.Key = stored.Key;
            entity.Type = stored.Type;
            entity.Default = stored.Default;
            entity.Description = stored.Description;
        }

        if (catalog.Find(entity.Key) is not { } declaration)
            throw new SparkValidationException($"'{entity.Key}' is not a declared setting.", nameof(Setting.Key));

        entity.Value = string.IsNullOrWhiteSpace(entity.Value) ? null : entity.Value.Trim();
        if (entity.Value is not null && !declaration.CanParse(entity.Value))
            throw new SparkValidationException(
                $"'{entity.Value}' is not a valid {SparkSettingValues.TypeName(declaration.ValueType)}.", nameof(Setting.Value));

        return ValueTask.CompletedTask;
    }

    public ValueTask OnAfterSaveAsync(Setting entity, SaveContext context)
    {
        settings.Invalidate();
        return ValueTask.CompletedTask;
    }

    public ValueTask OnAfterDeleteAsync(Setting entity, DeleteContext context)
    {
        settings.Invalidate();
        return ValueTask.CompletedTask;
    }
}
