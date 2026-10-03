using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;

namespace CodeCoverage.Tests._Infrastructure;

/// <summary>
/// Calls an Actions class's save hooks (#482: <see cref="IBeforeSave{T}"/>, <see cref="IAfterSave{T}"/>)
/// directly, the way these unit tests called <c>OnBeforeSaveAsync(obj, entity)</c> before: the rules
/// are judged on the entity, outside a framework save.
/// </summary>
internal static class HookCallExtensions
{
    public static Task BeforeSaveAsync<T>(this IBeforeSave<T> hook, PersistentObject obj, T entity) where T : class
        => hook.OnBeforeSaveAsync(entity, Context(obj, entity)).AsTask();

    public static Task AfterSaveAsync<T>(this IAfterSave<T> hook, PersistentObject obj, T entity) where T : class
        => hook.OnAfterSaveAsync(entity, Context(obj, entity)).AsTask();

    private static SaveContext Context<T>(PersistentObject obj, T entity) where T : class => new()
    {
        EntityType = typeof(T),
        Operation = string.IsNullOrEmpty(obj.Id) ? PersistentObjectOperation.New : PersistentObjectOperation.Save,
        PersistentObject = obj,
        Entity = entity,
        Session = new object(),
    };
}
