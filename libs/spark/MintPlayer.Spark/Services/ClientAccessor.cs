using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.ClientOperations;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Per-request accumulator for client operations. Drained by action endpoints into
/// the <see cref="ClientOperationEnvelope"/> on response egress.
/// </summary>
[Register(typeof(IClientAccessor), ServiceLifetime.Scoped)]
internal sealed partial class ClientAccessor : IClientAccessor
{
    private readonly List<ClientOperation> _operations = [];
    private readonly IRequestCultureResolver? cultureResolver;
    private readonly IModelLoader? modelLoader;
    private readonly IAttributeRightsEnforcement? attributeRights;

    public ClientAccessor() { }

    /// <summary>
    /// The DI constructor: the culture resolves the plain text of a translated notice, and the model
    /// and the caller's rights judge a refresh that names an attribute without an object.
    /// </summary>
    public ClientAccessor(IRequestCultureResolver cultureResolver, IModelLoader modelLoader, IAttributeRightsEnforcement attributeRights)
    {
        this.cultureResolver = cultureResolver;
        this.modelLoader = modelLoader;
        this.attributeRights = attributeRights;
    }

    public IReadOnlyList<ClientOperation> Operations => _operations;

    // --- Navigate -------------------------------------------------------

    public void Navigate(PersistentObject po)
    {
        ArgumentNullException.ThrowIfNull(po);
        if (string.IsNullOrEmpty(po.Id))
            throw new InvalidOperationException(
                "Cannot Navigate to a PersistentObject without an Id — typically means the PO hasn't been saved yet.");
        _operations.Add(new NavigateOperation { ObjectTypeId = po.ObjectTypeId, Id = po.Id });
    }

    public void Navigate(Guid objectTypeId, string id)
        => _operations.Add(new NavigateOperation { ObjectTypeId = objectTypeId, Id = id });

    public void Navigate(string routeName)
        => _operations.Add(new NavigateOperation { RouteName = routeName });

    // --- Notify ---------------------------------------------------------

    public void Notify(string message, NotificationKind kind = NotificationKind.Info, TimeSpan? duration = null)
        => _operations.Add(new NotifyOperation
        {
            Message = message,
            Kind = kind,
            DurationMs = duration is { } d ? (int)d.TotalMilliseconds : null,
        });

    public void Notify(TranslatedString message, NotificationKind kind = NotificationKind.Info, TimeSpan? duration = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        _operations.Add(new NotifyOperation
        {
            Message = message.GetValue(RequestCulture()),
            TranslatedMessage = message,
            Kind = kind,
            DurationMs = duration is { } d ? (int)d.TotalMilliseconds : null,
        });
    }

    /// <summary>The request's culture for the plain message; English without a request or a culture configuration.</summary>
    private string RequestCulture()
    {
        try
        {
            return cultureResolver?.GetCurrentCulture() ?? "en";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return "en";
        }
    }

    // --- Refresh --------------------------------------------------------

    public void RefreshAttribute(PersistentObject po, string attributeName)
    {
        ArgumentNullException.ThrowIfNull(po);
        if (string.IsNullOrEmpty(po.Id))
            throw new InvalidOperationException(
                "Cannot RefreshAttribute on a PersistentObject without an Id.");
        // An attribute the caller's rights removed from the object (D13a) is not refreshed: the patch
        // would name it. Silent, like a write to it; an attribute the type never had still throws.
        if (!po.TryGetAttribute(attributeName, out var attr))
        {
            _ = po[attributeName];
            return;
        }
        _operations.Add(new RefreshAttributeOperation
        {
            ObjectTypeId = po.ObjectTypeId,
            Id = po.Id,
            AttributeName = attributeName,
            Value = attr.Value,
            // An AsDetail attribute keeps its rows on the subclass and leaves Value null, so
            // copying only Value made this overload silently incapable of refreshing a detail
            // grid: it sent null for precisely the attributes an action is most likely to have
            // rewritten, and the client skips a null patched over a null.
            Object = attr is PersistentObjectAttributeAsDetail single ? single.Object : null,
            Objects = attr is PersistentObjectAttributeAsDetail many ? many.Objects : null,
        });
    }

    public void RefreshAttribute(Guid objectTypeId, string id, string attributeName, object? value)
    {
        // The object-less twin of the no-op above (composition M9): an attribute the caller may not
        // read is not refreshed, because the patch would name it and carry its value.
        if (IsReadDenied(objectTypeId, attributeName))
            return;
        _operations.Add(new RefreshAttributeOperation
        {
            ObjectTypeId = objectTypeId,
            Id = id,
            AttributeName = attributeName,
            Value = value,
        });
    }

    /// <summary>
    /// Whether the caller's static rights remove <paramref name="attributeName"/> from a read of the
    /// type. A type or attribute the model does not have throws, as the object overload does for an
    /// attribute the type never had. Blocks on the rights decision, as the boundary net does
    /// (<see cref="SparkPresentation"/>); the request's type check has normally made it already.
    /// Outside a request the caller is the system, and nothing is denied.
    /// </summary>
    private bool IsReadDenied(Guid objectTypeId, string attributeName)
    {
        if (modelLoader is null || attributeRights is null)
            return false;

        var definition = modelLoader.GetEntityType(objectTypeId)
            ?? throw new InvalidOperationException($"Cannot RefreshAttribute on type {objectTypeId}: the model has no such type.");
        if (!definition.Attributes.Any(a => string.Equals(a.Name, attributeName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Cannot RefreshAttribute '{attributeName}': type '{definition.Name}' has no such attribute.");

        return attributeRights.GetDeniedAsync(definition, Abstractions.Authorization.SparkCoreActions.Read)
            .GetAwaiter().GetResult()
            .Contains(attributeName);
    }

    public void RefreshQuery(string queryId)
        => _operations.Add(new RefreshQueryOperation { QueryId = queryId });


    // --- Framework-internal: retry push --------------------------------

    /// <summary>
    /// Pushes a <see cref="RetryOperation"/> onto the accumulator. Called by
    /// <see cref="RetryAccessor"/> just before it throws, so the retry rides
    /// out in the same envelope as any non-blocking operations queued earlier.
    /// </summary>
    internal void PushRetry(
        int step,
        string title,
        string[] options,
        string? defaultOption,
        PersistentObject? persistentObject,
        string? message)
        => _operations.Add(new RetryOperation
        {
            Step = step,
            Title = title,
            Options = options,
            DefaultOption = defaultOption,
            PersistentObject = persistentObject,
            Message = message,
        });
}
