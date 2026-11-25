using System.Globalization;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Relational.Model;

namespace MintPlayer.Spark.Relational.Storage;

/// <summary>The Data Protection key ring in table <c>SparkDataProtectionKeys</c> (#460 D5 on the relational providers).</summary>
public sealed class RelationalDataProtectionKeyStore : ISparkDataProtectionKeyStore
{
    private readonly RelationalSparkStore store;

    public RelationalDataProtectionKeyStore(RelationalSparkStore store) => this.store = store;

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var db = store.NewContext();
        return [.. db.Set<SparkDataProtectionKeyRow>().AsNoTracking().Select(r => r.Xml).AsEnumerable().Select(x => XElement.Parse(x))];
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var db = store.NewContext();
        db.Add(new SparkDataProtectionKeyRow
        {
            Name = string.IsNullOrEmpty(friendlyName) ? Guid.NewGuid().ToString("N") : friendlyName,
            Xml = element.ToString(SaveOptions.DisableFormatting),
        });
        db.SaveChanges();
    }
}

/// <summary>
/// Integer ids on the relational providers (#536 D23 rule 5): a HiLo over <see cref="ISparkAtomicValues"/>. Each
/// process reserves <see cref="RangeSize"/> keys of a collection at a time with one compare-and-swap on
/// <c>spark/hilo/{collection}</c>, so concurrent processes never hand out the same key. Unused keys of a range are
/// skipped when the process stops, as RavenDB's HiLo skips them.
/// </summary>
public sealed class RelationalIntegerIdSource : ISparkIntegerIdSource
{
    public const int RangeSize = 32;

    private readonly ISparkAtomicValues values;
    private readonly RelationalSparkStore store;
    private readonly Dictionary<string, (long Next, long Last)> ranges = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = new(1, 1);

    public RelationalIntegerIdSource(ISparkAtomicValues values, RelationalSparkStore store)
    {
        this.values = values;
        this.store = store;
    }

    public async Task<string> NextKeyAsync(Type entityType, CancellationToken cancellationToken)
    {
        var collection = store.GetCollectionName(entityType).ToLowerInvariant();
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!ranges.TryGetValue(collection, out var range) || range.Next > range.Last)
                range = await ReserveAsync(collection, cancellationToken);
            ranges[collection] = (range.Next + 1, range.Last);
            return range.Next.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<(long Next, long Last)> ReserveAsync(string collection, CancellationToken cancellationToken)
    {
        var key = "spark/hilo/" + collection;
        while (true)
        {
            var current = await values.GetAsync(key, cancellationToken);
            var high = current is null ? 0 : long.Parse(current.Value, NumberStyles.None, CultureInfo.InvariantCulture);
            var last = high + RangeSize;
            if (await values.TryPutAsync(key, last.ToString(CultureInfo.InvariantCulture), current?.Version ?? 0, cancellationToken) is not null)
                return (high + 1, last);
        }
    }
}
