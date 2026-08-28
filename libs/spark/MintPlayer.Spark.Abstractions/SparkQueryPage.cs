using System.Collections;

namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// The non-generic face of <see cref="SparkQueryPage{T}"/>, so the query executor can read the
/// author's total without reflecting over a closed generic.
/// </summary>
public interface ISparkQueryPage
{
    /// <summary>
    /// How many rows match <em>before</em> paging — the number the pager counts against, not the
    /// length of this page.
    /// </summary>
    int TotalItems { get; }
}

/// <summary>
/// A custom query method's way of saying <b>"I already did the work"</b>: these rows are the page,
/// and <paramref name="TotalItems"/> is the size of the full result they came from.
/// </summary>
/// <remarks>
/// <para>
/// <b>The authority rule is binary.</b> Either the framework owns filtering, search, sorting,
/// counting and paging, or the author does — never some of each. Returning a bare sequence keeps
/// all five with the framework; returning a <see cref="SparkQueryPage{T}"/> transfers all five to
/// the method, which then receives the request's skip/take/search/sort through
/// <see cref="CustomQueryArgs"/> and is responsible for honouring them.
/// </para>
/// <para>
/// The rule is binary because a half-delegated design fails invisibly. If the author pages and the
/// framework sorts, the framework sorts <em>the current page</em> and presents it as a global
/// ordering: the grid looks sorted, every page is internally ordered, and the sequence across pages
/// is wrong. Nothing about the result says so. The same applies to a framework `.Count()` over an
/// already-trimmed sequence — the pager then reports the page size as the total and offers one page.
/// </para>
/// <para>
/// The point of the escape hatch is a source the framework cannot page for you: an external API
/// that takes its own offset, an aggregate whose total is a separate query, a log store that only
/// answers in chunks.
/// </para>
/// <example>
/// <code>
/// public async Task&lt;SparkQueryPage&lt;LogRow&gt;&gt; GetLogs(CustomQueryArgs args)
/// {
///     var (rows, total) = await logApi.FetchAsync(args.Skip, args.Take, args.Search);
///     return new SparkQueryPage&lt;LogRow&gt;(rows, total);
/// }
/// </code>
/// </example>
/// </remarks>
/// <param name="Items">This page's rows, already filtered, searched, sorted and trimmed.</param>
/// <param name="TotalItems">How many rows match before paging.</param>
public sealed record SparkQueryPage<T>(IReadOnlyList<T> Items, int TotalItems)
    : ISparkQueryPage, IReadOnlyList<T>
{
    /// <summary>
    /// The page enumerates as its rows, so every shape-detection path that already understands a
    /// sequence of <typeparamref name="T"/> keeps working — the envelope adds a total, it does not
    /// introduce a second row contract.
    /// </summary>
    public IEnumerator<T> GetEnumerator() => Items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public int Count => Items.Count;
    public T this[int index] => Items[index];
}
