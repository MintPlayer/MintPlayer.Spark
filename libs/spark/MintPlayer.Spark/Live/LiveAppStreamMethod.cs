using System.Reflection;
using System.Runtime.CompilerServices;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Queries;

namespace MintPlayer.Spark.Live;

/// <summary>
/// An app-stream method on an Actions class: <c>(LiveQueryArgs, CancellationToken)</c> returning
/// <c>IAsyncEnumerable&lt;IReadOnlyList&lt;T&gt;&gt;</c> (whole sets) or <c>IAsyncEnumerable&lt;T&gt;</c>
/// (one row at a time).
/// </summary>
internal sealed class LiveAppStreamMethod
{
    public required MethodInfo Method { get; init; }
    public required Type ElementType { get; init; }
    public required bool IsSingleItemStream { get; init; }

    /// <summary>The method named <paramref name="methodName"/> on <paramref name="actionsType"/>, when it has the app-stream signature.</summary>
    public static LiveAppStreamMethod? Resolve(Type actionsType, string methodName)
        => ReflectionCache.GetOrAdd<(string Op, Type Type, string Method), LiveAppStreamMethod?>(
            ("LiveAppStreamMethod.Resolve", actionsType, methodName),
            static k =>
            {
                var method = k.Type.GetMethod(k.Method, BindingFlags.Public | BindingFlags.Instance);
                if (method is null)
                    return null;

                var parameters = method.GetParameters();
                if (parameters.Length != 2
                    || parameters[0].ParameterType != typeof(LiveQueryArgs)
                    || parameters[1].ParameterType != typeof(CancellationToken))
                    return null;

                var yielded = GenericArgumentOf(method.ReturnType, typeof(IAsyncEnumerable<>));
                if (yielded is null)
                    return null;

                return GenericArgumentOf(yielded, typeof(IReadOnlyList<>)) is { } batchElement
                    ? new LiveAppStreamMethod { Method = method, ElementType = batchElement, IsSingleItemStream = false }
                    : new LiveAppStreamMethod { Method = method, ElementType = yielded, IsSingleItemStream = true };
            });

    private static Type? GenericArgumentOf(Type type, Type openInterface)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == openInterface)
            return type.GetGenericArguments()[0];
        foreach (var iface in type.GetInterfaces())
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == openInterface)
                return iface.GetGenericArguments()[0];
        return null;
    }

    /// <summary>Invokes the method and enumerates what it yields, each yield as a list of rows.</summary>
    public async IAsyncEnumerable<IReadOnlyList<object>> InvokeAsync(object actions, LiveQueryArgs args, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = Method.Invoke(actions, Services.SparkHookInvocation.HookInvoke, binder: null, parameters: [args, cancellationToken], culture: null);
        if (result is null)
            yield break;

        var (getEnumerator, moveNext, current) = ReflectionCache.GetOrAdd<(string Op, Type Element, bool Single), (MethodInfo, MethodInfo, PropertyInfo)>(
            ("LiveAppStreamMethod.Enumerator", ElementType, IsSingleItemStream),
            static k =>
            {
                var inner = k.Single ? k.Element : typeof(IReadOnlyList<>).MakeGenericType(k.Element);
                var enumerable = typeof(IAsyncEnumerable<>).MakeGenericType(inner);
                var enumerator = typeof(IAsyncEnumerator<>).MakeGenericType(inner);
                return (enumerable.GetMethod("GetAsyncEnumerator")!, enumerator.GetMethod("MoveNextAsync")!, enumerator.GetProperty("Current")!);
            });

        var enumeratorInstance = getEnumerator.Invoke(result, [cancellationToken])!;
        var currentGetter = AccessorCache.GetGetter(current);
        try
        {
            while (true)
            {
                if (moveNext.Invoke(enumeratorInstance, []) is not ValueTask<bool> step)
                    throw new InvalidOperationException("Unexpected MoveNextAsync return type");
                if (!await step)
                    break;

                var item = currentGetter(enumeratorInstance);
                if (IsSingleItemStream)
                {
                    if (item is not null)
                        yield return [item];
                }
                else if (item is System.Collections.IEnumerable rows)
                {
                    yield return rows.Cast<object>().ToList();
                }
            }
        }
        finally
        {
            if (enumeratorInstance is IAsyncDisposable disposable)
                await disposable.DisposeAsync();
        }
    }

}
