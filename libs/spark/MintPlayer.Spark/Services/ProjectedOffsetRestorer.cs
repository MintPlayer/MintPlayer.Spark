using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Reflection;
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Restores the offset RavenDB strips from a <see cref="DateTimeOffset"/> when the value becomes a
/// scalar index field, by reading it back from the <c>{Name}Raw</c> wrapper the index also carries.
/// </summary>
/// <remarks>
/// <para>
/// See <see cref="SparkIndexValue{T}"/> for why the wrapper exists. This is the read half: a
/// projection served from a stored index field returns the flattened scalar, and the wrapper — a
/// nested object, never decomposed — still holds the original.
/// </para>
/// <para>
/// <strong>Double application is structurally impossible.</strong> The wrapper holds the original
/// value whole, so restoring is an assignment — nothing is computed from the flattened value, and
/// the flattened value is not even read. Running this twice, or against a row that is already
/// correct, assigns the same bytes again.
/// </para>
/// <para>
/// That is the reason the carrier is a wrapper rather than an offset fragment. A
/// <c>{Name}OffsetMinutes</c> companion would have to <em>reconstruct</em>
/// (<c>value.ToOffset(TimeSpan.FromMinutes(n))</c>), which is only safe while nobody reimplements it
/// as <c>AddHours</c> or <c>new DateTimeOffset(value.DateTime, offset)</c> — both of which reinterpret
/// the wall clock and <em>are</em> accumulative. It would also read a null companion as <c>+00:00</c>,
/// producing a confidently wrong value rather than a missing one. There is no arithmetic here to get
/// wrong.
/// </para>
/// <para>
/// Degrades silently and completely: a missing, null or unusable wrapper leaves the value exactly as
/// projected. That is not a compatibility concession — the wrapper is genuinely absent while RavenDB
/// rebuilds an index after a deploy, on every consumer, every time.
/// </para>
/// </remarks>
public interface IProjectedOffsetRestorer
{
    /// <summary>
    /// Rewrites each row's <see cref="DateTimeOffset"/> properties in place from their wrapper
    /// companions. A row type carrying no wrappers costs one cached dictionary lookup.
    /// </summary>
    void Restore(IReadOnlyList<object> rows);
}

[Register(typeof(IProjectedOffsetRestorer), ServiceLifetime.Scoped)]
internal sealed partial class ProjectedOffsetRestorer : IProjectedOffsetRestorer
{
    /// <summary>The suffix the index generator appends to build a wrapper's name.</summary>
    internal const string WrapperSuffix = "Raw";

    private sealed record Plan(PropertyInfo Target, PropertyInfo Wrapper, PropertyInfo Carried, bool IsCollection);

    // Keyed on the runtime row type, not on RowSecurityContext.ResultType: that is deliberately null
    // for a composed type and may be a base type on the streaming path, whereas row.GetType() is
    // always exactly the type carrying the wrappers.
    private static readonly ConcurrentDictionary<Type, Plan[]> Plans = new();

    // One line per (type, property) the first time a projection is seen without its wrapper. The app
    // author can act on this — regenerate, or add the companion to a hand-written index entity — and
    // a correct-looking-but-wrong timestamp should not be silent. Never per row.
    private static readonly ConcurrentDictionary<string, byte> Announced = new();

    public void Restore(IReadOnlyList<object> rows)
    {
        if (rows.Count == 0) return;

        foreach (var row in rows)
        {
            if (row is null) continue;

            foreach (var plan in Plans.GetOrAdd(row.GetType(), BuildPlan))
            {
                var wrapper = plan.Wrapper.GetValue(row);
                if (wrapper is null) continue;

                var carried = plan.Carried.GetValue(wrapper);
                if (carried is null) continue;

                if (plan.IsCollection)
                    RestoreCollection(row, plan, carried);
                else
                    plan.Target.SetValue(row, carried);
            }
        }
    }

    private static void RestoreCollection(object row, Plan plan, object carried)
    {
        if (plan.Target.GetValue(row) is not IList target || carried is not IList source) return;

        // A count mismatch means the two fields disagree about the row. Restoring a prefix would
        // leave the collection internally inconsistent, which is worse than leaving it alone.
        if (target.Count != source.Count) return;

        for (var i = 0; i < target.Count; i++)
            target[i] = source[i];
    }

    private static Plan[] BuildPlan(Type rowType)
    {
        // Only a projection carries wrappers. An entity type yields an empty plan once and costs a
        // dictionary hit forever after.
        var properties = rowType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var plans = new List<Plan>();

        foreach (var target in properties)
        {
            if (!CarriesDateTimeOffset(target.PropertyType)) continue;
            if (!target.CanRead || !target.CanWrite) continue;

            var wrapper = properties.FirstOrDefault(p =>
                p.Name == target.Name + WrapperSuffix && p.CanRead);

            if (wrapper is null)
            {
                AnnounceMissing(rowType, target);
                continue;
            }

            // The [IgnoreProperty] gate is what stops an ordinary domain property named e.g.
            // "StartsRaw" from silently steering "Starts" — the same disambiguator the sort
            // companion convention uses.
            if (!wrapper.IsIgnoredForSparkModel()) continue;

            var carried = wrapper.PropertyType.GetProperty(nameof(SparkIndexValue<object>.V));
            if (carried is null || !carried.CanRead) continue;

            plans.Add(new Plan(target, wrapper, carried, IsCollection: IsDateTimeOffsetCollection(target.PropertyType)));
        }

        return [.. plans];
    }

    private static bool CarriesDateTimeOffset(Type type)
        => Nullable.GetUnderlyingType(type) is { } inner
            ? inner == typeof(DateTimeOffset)
            : type == typeof(DateTimeOffset) || IsDateTimeOffsetCollection(type);

    private static bool IsDateTimeOffsetCollection(Type type)
    {
        if (type == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(type)) return false;

        var element = type.IsArray
            ? type.GetElementType()
            : type.IsGenericType ? type.GetGenericArguments().FirstOrDefault() : null;

        if (element is null) return false;
        return (Nullable.GetUnderlyingType(element) ?? element) == typeof(DateTimeOffset);
    }

    private static void AnnounceMissing(Type rowType, PropertyInfo target)
    {
        // Only for projection types: an entity type has no wrappers by design and would flood.
        if (rowType.GetCustomAttribute<FromIndexAttribute>() is null) return;

        if (!Announced.TryAdd($"{rowType.FullName}.{target.Name}", 0)) return;

        Console.WriteLine(
            $"Warning: '{rowType.Name}.{target.Name}' is a DateTimeOffset projected from an index with no " +
            $"'{target.Name}{WrapperSuffix}' companion, so its offset is lost (the instant is preserved). " +
            $"Regenerate the index, or add the companion to the hand-written index entity.");
    }
}
