using System.Linq.Expressions;
using System.Reflection;

namespace MintPlayer.Spark.Abstractions.Reflection;

/// <summary>
/// Compiled property accessors. Wraps a <see cref="PropertyInfo"/> in
/// <see cref="Expression.Lambda{TDelegate}(Expression, ParameterExpression[])"/>
/// to produce strongly-typed getters / setters that avoid the per-call overhead
/// of <see cref="PropertyInfo.GetValue(object?)"/> / <see cref="PropertyInfo.SetValue(object?, object?)"/>.
/// <para>
/// Built on top of <see cref="ReflectionCache"/>'s identity-keyed tier: each compiled
/// delegate is memoized per <see cref="PropertyInfo"/> instance for the lifetime of
/// the AppDomain. The CLR canonicalizes <c>PropertyInfo</c> within an
/// <c>AssemblyLoadContext</c>, so independent <c>typeof(T).GetProperty(name)</c> calls
/// hit the same cached delegate without string-key composition.
/// </para>
/// <para>
/// The setter expects <paramref name="value"/> to already be assignable to the
/// property type — type coercion (e.g. <c>Convert.ChangeType</c>, enum parsing,
/// JSON unwrapping) must happen <em>before</em> calling the setter. Reflection-based
/// callers that already do this coercion can swap in <see cref="GetSetter"/>
/// without behavior change.
/// </para>
/// </summary>
public static class AccessorCache
{
    /// <summary>
    /// Returns a compiled getter that reads <paramref name="property"/> from a
    /// boxed instance. Throws <see cref="ArgumentException"/> if the property is
    /// not readable.
    /// </summary>
    public static Func<object, object?> GetGetter(PropertyInfo property)
    {
        ArgumentNullException.ThrowIfNull(property);
        if (!property.CanRead)
            throw new ArgumentException($"Property '{property.DeclaringType?.FullName}.{property.Name}' is not readable.", nameof(property));

        return ReflectionCache.GetOrAdd<PropertyInfo, Func<object, object?>>(property, CompileGetter);
    }

    /// <summary>
    /// Returns a compiled setter that writes <paramref name="property"/> on a
    /// boxed instance. Throws <see cref="ArgumentException"/> if the property is
    /// not writable.
    /// </summary>
    public static Action<object, object?> GetSetter(PropertyInfo property)
    {
        ArgumentNullException.ThrowIfNull(property);
        if (!property.CanWrite)
            throw new ArgumentException($"Property '{property.DeclaringType?.FullName}.{property.Name}' is not writable.", nameof(property));

        return ReflectionCache.GetOrAdd<PropertyInfo, Action<object, object?>>(property, CompileSetter);
    }

    private static Func<object, object?> CompileGetter(PropertyInfo property)
    {
        // (object instance) => (object?)((TDeclaring)instance).Property
        var instance = Expression.Parameter(typeof(object), "instance");
        var typedInstance = Expression.Convert(instance, property.DeclaringType!);
        var access = Expression.Property(typedInstance, property);
        var boxed = Expression.Convert(access, typeof(object));
        return Expression.Lambda<Func<object, object?>>(boxed, instance).Compile();
    }

    private static Action<object, object?> CompileSetter(PropertyInfo property)
    {
        // (object instance, object? value) => ((TDeclaring)instance).Property = (TProperty)value
        var instance = Expression.Parameter(typeof(object), "instance");
        var value = Expression.Parameter(typeof(object), "value");
        var typedInstance = Expression.Convert(instance, property.DeclaringType!);
        var typedValue = Expression.Convert(value, property.PropertyType);
        var assign = Expression.Assign(Expression.Property(typedInstance, property), typedValue);
        return Expression.Lambda<Action<object, object?>>(assign, instance, value).Compile();
    }
}
