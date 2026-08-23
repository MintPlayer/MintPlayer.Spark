using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions.Authorization;

namespace MintPlayer.Spark.Testing;

/// <summary>
/// An <see cref="IAccessControl"/> for the two questions a <c>security.json</c> cannot answer:
/// <em>what did the code ask for</em>, and <em>decide by predicate</em>.
/// </summary>
/// <remarks>
/// Prefer <see cref="SparkTestSecurity"/>. A test that states its rights as a file is testing the
/// evaluation path production uses; one that swaps this in is testing the call sites instead.
/// That is worth doing precisely when the resource string is the subject — a misspelled action or
/// a target built from the wrong name is invisible to a grant list that was written to match it.
/// <para>
/// There is deliberately no <c>IPermissionService</c> double. It is four lines of string
/// concatenation, and faking it removes the one piece of logic a resource-string assertion needs
/// in order to stay honest.
/// </para>
/// </remarks>
public sealed class SparkTestAccessControl : IAccessControl
{
    private readonly Func<string, bool> _decide;
    private readonly List<string> _asked = [];

    private SparkTestAccessControl(Func<string, bool> decide) => _decide = decide;

    /// <summary>Every resource asked for, in order, including repeats.</summary>
    /// <remarks>
    /// Repeats are kept rather than deduplicated: "asked twice" is a finding — it is what a
    /// missing request-scoped memo looks like from the outside.
    /// </remarks>
    public IReadOnlyList<string> Asked => _asked;

    /// <summary>Allows everything, and records what was asked.</summary>
    public static SparkTestAccessControl AllowAll() => new(_ => true);

    /// <summary>Denies everything, and records what was asked.</summary>
    public static SparkTestAccessControl DenyAll() => new(_ => false);

    /// <summary>Allows exactly these resources, case-insensitively. No bundle expansion.</summary>
    public static SparkTestAccessControl Granting(params string[] resources)
    {
        var allowed = new HashSet<string>(resources, StringComparer.OrdinalIgnoreCase);
        return new SparkTestAccessControl(allowed.Contains);
    }

    /// <summary>Decides by predicate, for a rule no grant list expresses.</summary>
    public static SparkTestAccessControl Matching(Func<string, bool> decide) => new(decide);

    public Task<bool> IsAllowedAsync(string resource, CancellationToken cancellationToken = default)
    {
        lock (_asked)
            _asked.Add(resource);

        return Task.FromResult(_decide(resource));
    }
}

public static class SparkTestAccessControlExtensions
{
    /// <summary>
    /// Replaces the host's <see cref="IAccessControl"/> with <paramref name="accessControl"/>.
    /// </summary>
    /// <remarks>
    /// Removing the existing registration rather than appending: appending works only because DI
    /// resolves the last one, which makes the test's behaviour depend on registration order and
    /// breaks silently the day something registers after it.
    /// <para>
    /// Registered as the instance, so the caller keeps the reference it asserts on.
    /// </para>
    /// </remarks>
    public static IServiceCollection UseSparkTestAccessControl(
        this IServiceCollection services, SparkTestAccessControl accessControl)
    {
        services.RemoveAll<IAccessControl>();
        services.AddSingleton<IAccessControl>(accessControl);
        return services;
    }
}
