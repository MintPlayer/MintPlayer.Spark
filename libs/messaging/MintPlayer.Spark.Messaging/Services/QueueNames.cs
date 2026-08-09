using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Messaging.Abstractions;

namespace MintPlayer.Spark.Messaging.Services;

/// <summary>
/// Single owner of queue-name derivation and validation.
/// <para>
/// Both the producer (<see cref="MessageBus"/>) and the consumer
/// (<see cref="MessageSubscriptionManager"/>) derive a queue name independently, from
/// reflection alone — the manager discovers <c>IRecipient&lt;T&gt;</c> registrations and has
/// no visibility into anything the producer passed. They must therefore agree by
/// construction, which is why derivation and validation live together here rather than
/// being duplicated at each call site.
/// </para>
/// </summary>
internal static class QueueNames
{
    /// <summary>
    /// The queue a message type is published to and subscribed from: an explicit
    /// <see cref="MessageQueueAttribute"/> if present, otherwise derived from the CLR type.
    /// </summary>
    public static string ForMessageType(Type messageType)
    {
        var attribute = messageType.GetCachedCustomAttribute<MessageQueueAttribute>();
        return attribute?.QueueName ?? Derive(messageType);
    }

    /// <summary>
    /// Derives a valid queue name from any CLR type, deterministically.
    /// <para>
    /// <see cref="Type.FullName"/> of a <em>constructed generic</em> embeds its arguments
    /// as assembly-qualified names — <c>Ns.Message`1[[Ns.Arg, Asm, Version=…]]</c> — which
    /// carries '[', ']', ',', '=' and spaces, all rejected by <see cref="IsValid"/>. So we
    /// never call FullName on a constructed generic: the name is composed from the generic
    /// <em>definition's</em> FullName (always plain; its backtick and arity digit are
    /// permitted) plus each argument's own derived name. Recursion makes nested generics
    /// such as <c>Foo&lt;Bar&lt;Baz&gt;&gt;</c> safe at every depth, and keeps the
    /// non-generic case — the base case — byte-identical to FullName.
    /// </para>
    /// </summary>
    public static string Derive(Type type) => Sanitize(SafeName(type));

    private static string SafeName(Type type)
    {
        if (!type.IsGenericType)
            return type.FullName!;

        var definitionName = type.GetGenericTypeDefinition().FullName!;
        // '-' rather than ',': comma is one of the characters IsValid rejects.
        var argumentNames = string.Join("-", type.GetGenericArguments().Select(SafeName));
        return $"{definitionName}-{argumentNames}";
    }

    /// <summary>
    /// Last-resort guard so automatic derivation cannot produce an invalid name. Composition
    /// above is already safe for ordinary CLR types; this covers runtime-emitted ones
    /// (dynamic/EF proxies, reflection-emit) whose names carry unexpected characters. Turning
    /// a pathological name into an ugly-but-valid one beats faulting the host at startup.
    /// </summary>
    private static string Sanitize(string value)
    {
        if (IsValid(value))
            return value;

        return string.Create(value.Length, value, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
                span[i] = IsValidChar(source[i]) ? source[i] : '_';
        });
    }

    /// <summary>
    /// Whether a queue name is safe to interpolate into the subscription's RQL predicate.
    /// Applied as a hard failure only to developer-supplied names
    /// (<see cref="MessageQueueAttribute"/>, explicit broadcast overrides), where failing
    /// fast on a typo is the right behaviour.
    /// </summary>
    public static bool IsValid(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        foreach (var c in value)
        {
            if (!IsValidChar(c))
                return false;
        }
        return true;
    }

    // Standard identifier shape plus '+' (nested CLR types render as "Outer+Inner") and
    // '`' plus digits (generic arity). Critically disallowed: '\'', '\\', whitespace, and
    // anything else that could break out of the single-quoted RQL literal.
    private static bool IsValidChar(char c)
        => char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-' || c == '+' || c == '`';
}
