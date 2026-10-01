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

    public ClientAccessor() { }

    /// <summary>The DI constructor: the culture resolves the plain text of a translated notice.</summary>
    public ClientAccessor(IRequestCultureResolver cultureResolver) => this.cultureResolver = cultureResolver;

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
        var attr = po[attributeName];
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
        => _operations.Add(new RefreshAttributeOperation
        {
            ObjectTypeId = objectTypeId,
            Id = id,
            AttributeName = attributeName,
            Value = value,
        });

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
