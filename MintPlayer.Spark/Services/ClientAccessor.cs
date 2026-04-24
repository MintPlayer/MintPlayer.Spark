using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.ClientInstructions;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Per-request accumulator for client instructions. Drained by action endpoints into
/// the <see cref="ClientInstructionEnvelope"/> on response egress.
/// </summary>
[Register(typeof(IClientAccessor), ServiceLifetime.Scoped)]
internal sealed partial class ClientAccessor : IClientAccessor
{
    private readonly List<ClientInstruction> _instructions = [];

    public IReadOnlyList<ClientInstruction> Instructions => _instructions;

    // --- Navigate -------------------------------------------------------

    public void Navigate(PersistentObject po)
    {
        ArgumentNullException.ThrowIfNull(po);
        if (string.IsNullOrEmpty(po.Id))
            throw new InvalidOperationException(
                "Cannot Navigate to a PersistentObject without an Id — typically means the PO hasn't been saved yet.");
        _instructions.Add(new NavigateInstruction { ObjectTypeId = po.ObjectTypeId, Id = po.Id });
    }

    public void Navigate(Guid objectTypeId, string id)
        => _instructions.Add(new NavigateInstruction { ObjectTypeId = objectTypeId, Id = id });

    public void Navigate(string routeName)
        => _instructions.Add(new NavigateInstruction { RouteName = routeName });

    // --- Notify ---------------------------------------------------------

    public void Notify(string message, NotificationKind kind = NotificationKind.Info, TimeSpan? duration = null)
        => _instructions.Add(new NotifyInstruction
        {
            Message = message,
            Kind = kind,
            DurationMs = duration is { } d ? (int)d.TotalMilliseconds : null,
        });

    // --- Refresh --------------------------------------------------------

    public void RefreshAttribute(PersistentObject po, string attributeName)
    {
        ArgumentNullException.ThrowIfNull(po);
        if (string.IsNullOrEmpty(po.Id))
            throw new InvalidOperationException(
                "Cannot RefreshAttribute on a PersistentObject without an Id.");
        var attr = po[attributeName];
        _instructions.Add(new RefreshAttributeInstruction
        {
            ObjectTypeId = po.ObjectTypeId,
            Id = po.Id,
            AttributeName = attributeName,
            Value = attr.Value,
        });
    }

    public void RefreshAttribute(Guid objectTypeId, string id, string attributeName, object? value)
        => _instructions.Add(new RefreshAttributeInstruction
        {
            ObjectTypeId = objectTypeId,
            Id = id,
            AttributeName = attributeName,
            Value = value,
        });

    public void RefreshQuery(string queryId)
        => _instructions.Add(new RefreshQueryInstruction { QueryId = queryId });

    // --- DisableAction overloads ---------------------------------------

    public void DisableActionsOn(PersistentObject po, params string[] actionNames)
    {
        ArgumentNullException.ThrowIfNull(po);
        if (string.IsNullOrEmpty(po.Id))
            throw new InvalidOperationException(
                "Cannot DisableActionsOn a PersistentObject without an Id.");
        AddDisableForEach(actionNames, new PersistentObjectDisableTarget { ObjectTypeId = po.ObjectTypeId, Id = po.Id });
    }

    public void DisableActionsOn(Guid objectTypeId, string id, params string[] actionNames)
        => AddDisableForEach(actionNames, new PersistentObjectDisableTarget { ObjectTypeId = objectTypeId, Id = id });

    public void DisableQueryActions(string queryId, params string[] actionNames)
        => AddDisableForEach(actionNames, new QueryDisableTarget { QueryId = queryId });

    public void DisableActions(params string[] actionNames)
        => AddDisableForEach(actionNames, new CurrentResponseDisableTarget());

    public void DisableActionsForSession(params string[] actionNames)
        => AddDisableForEach(actionNames, new SessionDisableTarget());

    private void AddDisableForEach(string[] actionNames, DisableTarget target)
    {
        foreach (var name in actionNames)
            _instructions.Add(new DisableActionInstruction { ActionName = name, Target = target });
    }

    // --- Framework-internal: retry push --------------------------------

    /// <summary>
    /// Pushes a <see cref="RetryInstruction"/> onto the accumulator. Called by
    /// <see cref="RetryAccessor"/> just before it throws, so the retry rides
    /// out in the same envelope as any non-blocking instructions queued earlier.
    /// </summary>
    internal void PushRetry(
        int step,
        string title,
        string[] options,
        string? defaultOption,
        PersistentObject? persistentObject,
        string? message)
        => _instructions.Add(new RetryInstruction
        {
            Step = step,
            Title = title,
            Options = options,
            DefaultOption = defaultOption,
            PersistentObject = persistentObject,
            Message = message,
        });
}
