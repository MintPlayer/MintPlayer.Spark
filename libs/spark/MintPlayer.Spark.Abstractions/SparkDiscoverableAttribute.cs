using Microsoft.Extensions.DependencyInjection;

namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Marks a contract interface whose implementations the application's generated
/// <c>spark.AddSparkDiscovered()</c> registers on its own (X20 item 3): every non-abstract,
/// non-generic, non-private class in the application that implements the interface — open generic
/// interfaces included, once per closed form — is added to DI as that interface.
/// </summary>
/// <remarks>
/// <para>
/// Registration uses <c>TryAddEnumerable</c>, so several implementations of one interface all
/// register, and a package that also registers an implementation by hand does not get it twice.
/// </para>
/// <para>
/// Only classes in the compilation that runs the generator are found, the same as Actions classes
/// and recipients: a library still registers its own implementations itself.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [SparkDiscoverable]
/// public interface IReplicaChanged&lt;T&gt; { Task OnChangedAsync(T replica, CancellationToken ct); }
///
/// // In the app: registered as IReplicaChanged&lt;Product&gt;, scoped, with no registration code.
/// public class ProductChanged : IReplicaChanged&lt;Product&gt; { … }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Interface, Inherited = false, AllowMultiple = false)]
public sealed class SparkDiscoverableAttribute : Attribute
{
    /// <summary>The lifetime each implementation is registered with. Default <see cref="ServiceLifetime.Scoped"/>.</summary>
    public ServiceLifetime Lifetime { get; set; } = ServiceLifetime.Scoped;
}
