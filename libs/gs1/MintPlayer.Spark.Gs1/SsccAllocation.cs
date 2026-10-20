using System.Collections.Concurrent;

namespace MintPlayer.Spark.Gs1;

/// <summary>
/// Hands out serial references for SSCCs, once each (docs/warehouse_picking_PRD.md §4.7 Packing, step 7). One sequence per
/// (extension digit, company prefix), named by <see cref="SsccAllocator.SequenceKey"/>.
/// </summary>
/// <remarks>
/// This package has no RavenDB dependency (§4.1). The production source is Spark core's compare-exchange sequence
/// (<c>ISparkSequences</c>, bound through <see cref="DelegateSsccSerialSource"/>): read the value under
/// <see cref="SsccAllocator.SequenceKey"/>, write value + 1 with the read index, retry on a lost race, so that two pack
/// stations never get the same serial, even on a cluster.
/// </remarks>
public interface ISsccSerialSource
{
    /// <summary>The next unused serial reference of the sequence: 0, 1, 2… Never the same value twice.</summary>
    ValueTask<long> NextAsync(string sequenceKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// An <see cref="ISsccSerialSource"/> over any counter, without a dependency on it. The production wiring is Spark's
/// compare-exchange sequence (<c>MintPlayer.Spark.Sequences.ISparkSequences</c>):
/// <code>
/// new SsccAllocator(new DelegateSsccSerialSource((key, ct) => new(sequences.NextAsync(key, 0, ct))), 0, prefix);
/// </code>
/// </summary>
public sealed class DelegateSsccSerialSource(Func<string, CancellationToken, ValueTask<long>> next) : ISsccSerialSource
{
    private readonly Func<string, CancellationToken, ValueTask<long>> next = next ?? throw new ArgumentNullException(nameof(next));

    public ValueTask<long> NextAsync(string sequenceKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sequenceKey);
        return next(sequenceKey, cancellationToken);
    }
}

/// <summary>An in-process <see cref="ISsccSerialSource"/>, for tests and single-process tools. It forgets on restart.</summary>
public sealed class InMemorySsccSerialSource : ISsccSerialSource
{
    private readonly ConcurrentDictionary<string, StrongBox> sequences = new(StringComparer.Ordinal);

    /// <summary>Starts every sequence at <paramref name="firstSerial"/>.</summary>
    public InMemorySsccSerialSource(long firstSerial = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstSerial);
        FirstSerial = firstSerial;
    }

    public long FirstSerial { get; }

    public ValueTask<long> NextAsync(string sequenceKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sequenceKey);
        cancellationToken.ThrowIfCancellationRequested();
        var box = sequences.GetOrAdd(sequenceKey, _ => new StrongBox(FirstSerial - 1));
        return ValueTask.FromResult(Interlocked.Increment(ref box.Value));
    }

    private sealed class StrongBox(long value)
    {
        public long Value = value;
    }
}

/// <summary>
/// Allocates SSCCs (AI 00) for one GS1 Company Prefix and extension digit: a serial from <see cref="ISsccSerialSource"/>,
/// formatted by <see cref="Gs1Sscc.Create"/> with its check digit.
/// </summary>
public sealed class SsccAllocator
{
    private readonly ISsccSerialSource source;

    /// <param name="extensionDigit">0–9; chosen by the company to widen its serial range.</param>
    /// <param name="companyPrefix">The GS1 Company Prefix, 7–10 digits, as issued by a GS1 member organisation.</param>
    /// <exception cref="ArgumentException">The extension digit or the company prefix is out of range.</exception>
    public SsccAllocator(ISsccSerialSource source, int extensionDigit, string companyPrefix)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(extensionDigit, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(extensionDigit, 9);
        ArgumentNullException.ThrowIfNull(companyPrefix);
        if (companyPrefix.Length is < 7 or > 10 || !companyPrefix.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("A GS1 Company Prefix is 7 to 10 digits.", nameof(companyPrefix));
        }

        this.source = source;
        ExtensionDigit = extensionDigit;
        CompanyPrefix = companyPrefix;
        MaxSerial = (long)Math.Pow(10, 16 - companyPrefix.Length) - 1;
        SequenceKey = KeyFor(extensionDigit, companyPrefix);
    }

    public int ExtensionDigit { get; }

    public string CompanyPrefix { get; }

    /// <summary>The largest serial reference that fits: 16 − prefix length digits.</summary>
    public long MaxSerial { get; }

    /// <summary>The sequence's name, <c>gs1/sscc/{extensionDigit}{companyPrefix}</c>; also the compare-exchange key.</summary>
    public string SequenceKey { get; }

    /// <summary>The sequence name for an extension digit and company prefix.</summary>
    public static string KeyFor(int extensionDigit, string companyPrefix) => $"gs1/sscc/{extensionDigit}{companyPrefix}";

    /// <summary>The next SSCC, 18 digits.</summary>
    /// <exception cref="InvalidOperationException">The serial range of this prefix and extension digit is used up.</exception>
    public async ValueTask<string> AllocateAsync(CancellationToken cancellationToken = default)
    {
        var serial = await source.NextAsync(SequenceKey, cancellationToken).ConfigureAwait(false);
        if (serial < 0 || serial > MaxSerial)
        {
            throw new InvalidOperationException(
                $"The SSCC serial range of {SequenceKey} is used up (serial {serial}, maximum {MaxSerial}). Use another extension digit.");
        }
        return Gs1Sscc.Create(ExtensionDigit, CompanyPrefix, serial);
    }
}
