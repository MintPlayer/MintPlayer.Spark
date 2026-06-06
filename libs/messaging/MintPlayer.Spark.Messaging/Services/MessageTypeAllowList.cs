using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Messaging.Abstractions;

namespace MintPlayer.Spark.Messaging.Services;

/// <summary>
/// R2-H6: allowlist of CLR types that the messaging worker will pass to
/// <c>Type.GetType</c> + <c>JsonConvert.DeserializeObject</c>. The set is
/// derived once at startup from registered <c>IRecipient&lt;T&gt;</c> services
/// — anything not on this list is dead-lettered before we call into reflection,
/// closing the polymorphic-deserialization gadget surface that arbitrary
/// <c>MessageType</c> strings (writable via the unauthenticated
/// <c>/spark/sync/apply</c> endpoint pre-mTLS) would otherwise open.
/// </summary>
internal interface IMessageTypeAllowList
{
    /// <summary>Returns true if <paramref name="assemblyQualifiedName"/> is a known message type.</summary>
    bool IsAllowedMessageType(string? assemblyQualifiedName);

    /// <summary>Returns true if <paramref name="assemblyQualifiedName"/> is a known recipient implementation.</summary>
    bool IsAllowedHandlerType(string? assemblyQualifiedName);
}

internal sealed class MessageTypeAllowList : IMessageTypeAllowList
{
    private readonly HashSet<string> _allowedMessageTypes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _allowedHandlerTypes = new(StringComparer.Ordinal);

    public MessageTypeAllowList(IServiceCollectionAccessor accessor)
    {
        var descriptors = accessor?.Services;
        if (descriptors is null) return;

        foreach (var descriptor in descriptors)
        {
            var serviceType = descriptor.ServiceType;
            if (!serviceType.IsGenericType || serviceType.GetGenericTypeDefinition() != typeof(IRecipient<>))
                continue;

            // Allowed message types: the T in IRecipient<T>.
            var messageType = serviceType.GetGenericArguments()[0];
            if (messageType.AssemblyQualifiedName is { } mAqn)
                _allowedMessageTypes.Add(mAqn);

            // Allowed handler types: the registered implementation. We accept any
            // class that's been wired to satisfy IRecipient<T> in this app.
            var implType = descriptor.ImplementationType
                ?? descriptor.ImplementationInstance?.GetType()
                ?? descriptor.KeyedImplementationType;
            if (implType?.AssemblyQualifiedName is { } hAqn)
                _allowedHandlerTypes.Add(hAqn);
        }
    }

    public bool IsAllowedMessageType(string? assemblyQualifiedName)
        => !string.IsNullOrEmpty(assemblyQualifiedName) && _allowedMessageTypes.Contains(assemblyQualifiedName);

    public bool IsAllowedHandlerType(string? assemblyQualifiedName)
        => !string.IsNullOrEmpty(assemblyQualifiedName) && _allowedHandlerTypes.Contains(assemblyQualifiedName);
}
