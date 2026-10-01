using System.Collections;
using System.Collections.Immutable;

// netstandard2.0 has no IsExternalInit; records and init accessors need it to compile.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}

namespace MintPlayer.Spark.Contributions.SourceGenerators
{
    /// <summary>
    /// An array with value equality, so a model that carries one compares by contents and the
    /// incremental pipeline can skip the output step when nothing changed. <see cref="ImmutableArray{T}"/>
    /// compares by reference.
    /// </summary>
    internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
        where T : IEquatable<T>
    {
        private readonly T[]? items;

        public EquatableArray(IEnumerable<T> items) => this.items = items.ToArray();

        public int Length => items?.Length ?? 0;

        public T this[int index] => items![index];

        public bool Equals(EquatableArray<T> other)
        {
            var left = items ?? [];
            var right = other.items ?? [];
            if (left.Length != right.Length)
                return false;
            for (var i = 0; i < left.Length; i++)
            {
                if (!left[i].Equals(right[i]))
                    return false;
            }
            return true;
        }

        public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

        public override int GetHashCode()
        {
            var hash = 17;
            foreach (var item in items ?? [])
                hash = unchecked(hash * 31 + item.GetHashCode());
            return hash;
        }

        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(items ?? [])).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
