using System.Runtime.CompilerServices;

namespace MintPlayer.Spark;

/// <summary>
/// Which hard deletes bypass the persistence hooks knowingly (#467, D32). A library whose rows must
/// never be hard-deleted behind its back (SoftDelete) refuses a raw session delete unless the framework
/// issued it or the code opted out with <see cref="Allow"/>.
/// </summary>
/// <remarks>
/// A guard, not a wall: RavenDB patches and delete-by-query operations raise no session event.
/// </remarks>
public static class SparkRawWrites
{
    private static readonly AsyncLocal<int> allowDepth = new();
    private static readonly ConditionalWeakTable<object, HashSet<string>> issued = new();

    /// <summary>
    /// Allows raw writes — a migration, a test fixture, a maintenance job — until the returned scope
    /// is disposed. Wrap the <c>SaveChangesAsync</c>, not only the <c>Delete</c>: the guard runs when
    /// the session commits.
    /// </summary>
    public static IDisposable Allow()
    {
        allowDepth.Value++;
        return new Scope();
    }

    /// <summary>
    /// Whether a hard delete of <paramref name="id"/> committed through <paramref name="session"/> is
    /// allowed: inside <see cref="Allow"/>, or issued by the framework's own persister.
    /// </summary>
    public static bool IsAllowed(object session, string id)
    {
        if (allowDepth.Value > 0)
            return true;
        if (!issued.TryGetValue(session, out var ids))
            return false;
        lock (ids)
            return ids.Contains(id);
    }

    /// <summary>The persister is about to delete <paramref name="id"/> through <paramref name="session"/>.</summary>
    internal static void IssuedByFramework(object session, string id)
    {
        var ids = issued.GetValue(session, static _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        lock (ids)
            ids.Add(id);
    }

    private sealed class Scope : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            allowDepth.Value--;
        }
    }
}
