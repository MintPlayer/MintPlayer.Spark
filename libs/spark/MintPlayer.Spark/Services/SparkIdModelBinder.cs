using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Binds an <see cref="Id{T}"/> (or <c>Id&lt;T&gt;?</c>) controller parameter through the app model (#536 D7, D23
/// rule 3): the text is canonicalised by <see cref="ISparkIds.Canonicalize"/>, so a hand-typed <c>ABC</c> finds the
/// entity stored as <c>abc</c> and the key alone is enough. Text that is not an id of the entity, such as another
/// collection's id, is a model error (400), never an id of another collection.
/// </summary>
/// <remarks>
/// Minimal-API handlers take the text and call <see cref="ISparkIds.Parse{T}"/> themselves: a <c>BindAsync</c> on
/// <see cref="Id{T}"/> would put ASP.NET types in the attributes assembly.
/// </remarks>
internal sealed class SparkIdModelBinderProvider : IModelBinderProvider
{
    private static readonly ConcurrentDictionary<Type, SparkIdModelBinder?> Binders = new();

    public IModelBinder? GetBinder(ModelBinderProviderContext context)
        => Binders.GetOrAdd(Nullable.GetUnderlyingType(context.Metadata.ModelType) ?? context.Metadata.ModelType, Create);

    private static SparkIdModelBinder? Create(Type type)
    {
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(Id<>)) return null;
        var fromTrusted = type.GetMethod(nameof(Id<object>.FromTrusted), BindingFlags.Public | BindingFlags.Static, [typeof(string)])
            ?? throw new InvalidOperationException($"{type} has no FromTrusted(string).");
        return new SparkIdModelBinder(type.GetGenericArguments()[0], value => fromTrusted.Invoke(null, [value])!);
    }
}

internal sealed class SparkIdModelBinder(Type entityType, Func<string, object> create) : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var result = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
        if (result == ValueProviderResult.None) return Task.CompletedTask;
        bindingContext.ModelState.SetModelValue(bindingContext.ModelName, result);

        var text = result.FirstValue;
        if (string.IsNullOrWhiteSpace(text)) return Task.CompletedTask;

        var ids = (ISparkIds?)bindingContext.HttpContext.RequestServices.GetService(typeof(ISparkIds))
            ?? throw new InvalidOperationException("ISparkIds is not registered; call AddSpark.");
        if (ids.Canonicalize(entityType, text) is { } canonical)
            bindingContext.Result = ModelBindingResult.Success(create(canonical));
        else
            bindingContext.ModelState.TryAddModelError(bindingContext.ModelName, $"'{text}' is not an id of {entityType.Name}.");
        return Task.CompletedTask;
    }
}
