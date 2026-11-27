using System.Collections;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using MintPlayer.Spark.Relational.Model;

namespace MintPlayer.Spark.Relational.Storage;

/// <summary>
/// D25: a child table's list loads in the order it was stored. EF loads an owned collection in key order, and an owned
/// navigation takes no ordered include. So the materializer notes each child row's <see cref="SparkDbContext.PositionProperty"/>,
/// and the session sorts the lists it has just loaded by it.
/// </summary>
internal static class SparkListPositions
{
    private static readonly ConditionalWeakTable<object, StrongBox<int>> positions = new();

    /// <summary>The one instance every <see cref="SparkDbContext"/> adds: EF keeps a singleton interceptor per service provider.</summary>
    public static IInterceptor Interceptor { get; } = new PositionInterceptor();

    /// <summary>
    /// Sorts each child list of <paramref name="row"/> by its rows' stored positions. A list holding any row that was not
    /// just materialized is left alone: the session already tracked it, and its order may be an unsaved edit.
    /// </summary>
    public static void Sort(IModel model, object row)
    {
        if (model.FindEntityType(row.GetType()) is not { } type)
            return;
        foreach (var navigation in type.GetNavigations().Where(n => n.IsCollection && n.TargetEntityType.IsOwned()))
        {
            if (navigation.PropertyInfo?.GetValue(row) is not IList { IsReadOnly: false } list)
                continue;
            var items = new List<(object Item, int Position)>(list.Count);
            foreach (var item in list)
                if (item is not null && positions.TryGetValue(item, out var position))
                    items.Add((item, position.Value));
            foreach (var (item, _) in items)
                positions.Remove(item);
            if (items.Count != list.Count || items.Count < 2)
                continue;
            var index = 0;
            foreach (var (item, _) in items.OrderBy(i => i.Position)) // stable: equal positions keep the key order
                list[index++] = item;
        }
    }

    private sealed class PositionInterceptor : IMaterializationInterceptor
    {
        public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
        {
            if (materializationData.EntityType.IsOwned()
                && materializationData.EntityType.FindProperty(SparkDbContext.PositionProperty) is { } property)
                positions.AddOrUpdate(entity, new StrongBox<int>(materializationData.GetPropertyValue<int>(property)));
            return entity;
        }
    }
}
