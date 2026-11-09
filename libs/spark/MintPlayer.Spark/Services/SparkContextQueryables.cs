namespace MintPlayer.Spark.Services;

/// <summary>
/// Which <see cref="SparkContext"/> properties are query roots (#551 M5). A root is any property whose type is
/// <see cref="IQueryable{T}"/> or implements it: <c>IQueryable&lt;Person&gt; People =&gt; Session.Query&lt;Person&gt;()</c>,
/// and equally a provider's own queryable type (RavenDB's <c>IRavenQueryable&lt;T&gt;</c>). Before #551 only
/// <c>IRavenQueryable&lt;&gt;</c> counted, which tied every context to RavenDB.
/// </summary>
internal static class SparkContextQueryables
{
    /// <summary>The <c>T</c> of a query-root property type, or <see langword="null"/> when it is not one.</summary>
    public static Type? ElementType(Type propertyType)
    {
        if (propertyType.IsGenericType && propertyType.GetGenericTypeDefinition() == typeof(IQueryable<>))
            return propertyType.GetGenericArguments()[0];
        if (!propertyType.IsInterface && !propertyType.IsClass)
            return null;
        foreach (var iface in propertyType.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IQueryable<>))
                return iface.GetGenericArguments()[0];
        }
        return null;
    }

    /// <summary>Whether <paramref name="propertyType"/> is a query root.</summary>
    public static bool IsQueryRoot(Type propertyType) => ElementType(propertyType) is not null;
}
