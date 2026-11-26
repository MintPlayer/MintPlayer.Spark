using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Abstractions.Search;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Search;

/// <summary>
/// The provider of every store but RavenDB (#536 S10, D44): SQL Server, PostgreSQL and memory, where no full-text index is
/// generated. Each searchable type's collection is narrowed in the store, every word a case-insensitive substring of one of
/// the type's search fields (<c>LIKE</c> on SQL, a query parameter per word), and each row then matches in memory as
/// RavenDB's index would: every word a whole word of the text, the last one a prefix while typing.
/// </summary>
/// <remarks>
/// The search fields are the generator's (<c>SearchIndexGenerator</c> in the source generators): the placeholders of
/// <c>search.label</c> (else of the breadcrumb), then <c>search.fields</c>, when they hold a string or a collection of
/// strings. Soft-deleted rows (<c>IsDeleted</c>) never match. No typo tolerance: fuzzy search needs RavenDB's index.
/// Results come best first: more words matched whole, then the type's order, then natural id order.
/// </remarks>
internal sealed partial class StoreSearchProvider : ISparkSearchProvider
{
    /// <summary>Rows read first per wanted candidate: a substring that is not a whole word is dropped after the read.</summary>
    internal const int Prefetch = 4;

    /// <summary>
    /// The most rows a search reads of one type. Below it, a type is read page by page, in key order, until it has as
    /// many best matches as wanted (every word whole) or its rows run out, so a whole word is found however many rows
    /// hold it only inside a longer word.
    /// </summary>
    internal const int MaxRows = 5000;

    [Inject] private readonly ISparkStore store;
    [Inject] private readonly IModelLoader models;
    [Inject] private readonly ISparkTypeResolver typeResolver;

    private readonly ConcurrentDictionary<string, SearchSpec?> specs = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<SparkSearchCandidate>> SearchAsync(SparkSearchRequest request, CancellationToken cancellationToken)
        => FindAsync(SearchTerms.Words(request.Term), request.EntityTypes, request.Take, request.Prefix, cancellationToken);

    public Task<IReadOnlyList<SparkSearchCandidate>> FindByLabelAsync(string label, IReadOnlyCollection<string> entityTypes, CancellationToken cancellationToken)
        => FindAsync(SearchTerms.Words(label), entityTypes, RavenSearchProvider.FindByLabelTake, prefix: false, cancellationToken);

    private async Task<IReadOnlyList<SparkSearchCandidate>> FindAsync(IReadOnlyList<string> words, IReadOnlyCollection<string> entityTypes,
        int take, bool prefix, CancellationToken cancellationToken)
    {
        if (words.Count == 0 || entityTypes.Count == 0 || take <= 0)
            return [];

        var found = new List<(SparkSearchCandidate Candidate, int Whole, int TypeOrder)>();
        using var session = store.OpenSession();
        var order = 0;
        foreach (var typeName in entityTypes)
        {
            order++;
            if (specs.GetOrAdd(typeName, Describe) is not { } spec)
                continue;

            var best = 0;
            var read = 0;
            for (var page = Math.Min(take * Prefetch, MaxRows); ; page = Math.Min(page * 2, MaxRows - read))
            {
                var rows = await spec.ReadAsync(session, words, read, page, cancellationToken);
                foreach (var row in rows)
                {
                    if ((session.GetId(row) ?? SparkIdValues.ReadEntityId(row)) is not { Length: > 0 } id)
                        continue;
                    var whole = Matches(spec.Text(row), words, prefix);
                    if (whole < 0)
                        continue;
                    found.Add((new SparkSearchCandidate(typeName, id), whole, order));
                    if (whole == words.Count)
                        best++;
                }
                read += rows.Count;
                if (rows.Count < page || best >= take || read >= MaxRows)
                    break;
            }
        }

        return found
            .OrderByDescending(f => f.Whole)
            .ThenBy(f => f.TypeOrder)
            .ThenBy(f => f.Candidate.Id, NaturalIdComparer.Instance)
            .Take(take)
            .Select(f => f.Candidate)
            .ToList();
    }

    /// <summary>
    /// How many of <paramref name="words"/> are whole words of <paramref name="text"/>, or -1 when one is not: every word
    /// must be a word of the text, except that the last may be the start of one when <paramref name="prefix"/>.
    /// </summary>
    internal static int Matches(IEnumerable<string> text, IReadOnlyList<string> words, bool prefix)
    {
        var tokens = text.SelectMany(t => SearchTerms.Words(t, int.MaxValue)).ToHashSet(StringComparer.Ordinal);
        var whole = 0;
        for (var i = 0; i < words.Count; i++)
        {
            if (tokens.Contains(words[i]))
                whole++;
            else if (!(prefix && i == words.Count - 1 && tokens.Any(t => t.StartsWith(words[i], StringComparison.Ordinal))))
                return -1;
        }
        return whole;
    }

    private SearchSpec? Describe(string typeName)
    {
        if (models.GetEntityTypes().FirstOrDefault(t => t.Name == typeName) is not { Search.Enabled: true } definition
            || typeResolver.Resolve(definition.ClrType) is not { } clrType)
            return null;

        var template = definition.Search!.Label is { Length: > 0 } label ? label : definition.Breadcrumb;
        var names = (template is null ? [] : Placeholder().Matches(template).Select(m => m.Groups[1].Value))
            .Concat(definition.Search.Fields ?? [])
            .Distinct(StringComparer.Ordinal);

        var fields = new List<PropertyInfo>();
        foreach (var name in names)
        {
            if (clrType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is not { CanRead: true } property)
                continue;
            if (property.PropertyType == typeof(string) || StringElements(property.PropertyType))
                fields.Add(property);
        }
        if (fields.Count == 0)
            return null;

        var deleted = clrType.GetProperty("IsDeleted", BindingFlags.Public | BindingFlags.Instance) is { CanRead: true } d
            && (d.PropertyType == typeof(bool) || d.PropertyType == typeof(bool?)) ? d : null;
        return (SearchSpec)Activator.CreateInstance(typeof(SearchSpec<>).MakeGenericType(clrType), fields, deleted)!;
    }

    private static bool StringElements(Type type)
        => type != typeof(string) && type.GetInterfaces().Append(type)
            .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>) && i.GetGenericArguments()[0] == typeof(string));

    [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    private abstract class SearchSpec(IReadOnlyList<PropertyInfo> fields)
    {
        /// <summary>The rows that hold every word as a substring, in key order, from <paramref name="skip"/> on.</summary>
        public abstract Task<IReadOnlyList<object>> ReadAsync(ISparkSession session, IReadOnlyList<string> words, int skip, int take, CancellationToken cancellationToken);

        /// <summary>The searched text of a row, field by field.</summary>
        public IEnumerable<string> Text(object row)
        {
            foreach (var field in fields)
            {
                switch (field.GetValue(row))
                {
                    case string text:
                        yield return text;
                        break;
                    case IEnumerable<string> list:
                        foreach (var item in list)
                            if (item is not null)
                                yield return item;
                        break;
                }
            }
        }
    }

    private sealed class SearchSpec<T>(IReadOnlyList<PropertyInfo> fields, PropertyInfo? deleted) : SearchSpec(fields) where T : class
    {
        private static readonly MethodInfo ToLower = typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;
        private static readonly MethodInfo Contains = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;

        /// <summary>
        /// Orders by the key, typed (<c>x =&gt; x.Id</c>): pages are only stable in a fixed order, and memory compares an
        /// <see cref="Id{T}"/> through its own <see cref="IComparable{T}"/>.
        /// </summary>
        private static readonly Func<IQueryable<T>, IQueryable<T>> ByKey = typeof(T).GetProperty("Id", BindingFlags.Public | BindingFlags.Instance) is { CanRead: true } id
            ? OrderBy(id)
            : static query => query;

        public override async Task<IReadOnlyList<object>> ReadAsync(ISparkSession session, IReadOnlyList<string> words, int skip, int take, CancellationToken cancellationToken)
        {
            var query = ByKey(session.Query<T>().Where(Filter(words)));
            var rows = await session.ToListAsync(query.Skip(skip).Take(take), cancellationToken);
            return rows;
        }

        private static Func<IQueryable<T>, IQueryable<T>> OrderBy(PropertyInfo id)
        {
            var x = Expression.Parameter(typeof(T), "x");
            var key = Expression.Lambda(Expression.Property(x, id), x);
            var orderBy = typeof(Queryable).GetMethods()
                .Single(m => m.Name == nameof(Queryable.OrderBy) && m.GetParameters().Length == 2)
                .MakeGenericMethod(typeof(T), id.PropertyType);
            return query => (IQueryable<T>)orderBy.Invoke(null, [query, key])!;
        }

        /// <summary>
        /// Every word in one of the fields, ignoring case, and not soft-deleted. Each word is a captured value, so the store
        /// sends it as a query parameter, never as SQL text.
        /// </summary>
        private Expression<Func<T, bool>> Filter(IReadOnlyList<string> words)
        {
            var x = Expression.Parameter(typeof(T), "x");
            Expression? all = deleted is null
                ? null
                : Expression.NotEqual(Expression.Convert(Expression.Property(x, deleted), typeof(bool?)), Expression.Constant(true, typeof(bool?)));
            foreach (var word in words)
            {
                var value = Expression.Property(Expression.Constant(new Captured(word)), nameof(Captured.Value));
                Expression? any = null;
                foreach (var field in Fields)
                {
                    var member = Expression.Property(x, field);
                    var match = field.PropertyType == typeof(string) ? Substring(member, value) : InAny(member, value);
                    any = any is null ? match : Expression.OrElse(any, match);
                }
                all = all is null ? any! : Expression.AndAlso(all, any!);
            }
            return Expression.Lambda<Func<T, bool>>(all!, x);
        }

        private IReadOnlyList<PropertyInfo> Fields { get; } = fields;

        private static Expression Substring(Expression text, Expression word)
            => Expression.AndAlso(
                Expression.NotEqual(text, Expression.Constant(null, typeof(string))),
                Expression.Call(Expression.Call(text, ToLower), Contains, word));

        private static Expression InAny(MemberExpression list, Expression word)
        {
            var item = Expression.Parameter(typeof(string), "t");
            var any = Expression.Call(typeof(Enumerable), nameof(Enumerable.Any), [typeof(string)],
                list, Expression.Lambda(Substring(item, word), item));
            return Expression.AndAlso(Expression.NotEqual(list, Expression.Constant(null, list.Type)), any);
        }
    }

    /// <summary>A word as a closure member: a store translates a member of a constant into a query parameter.</summary>
    private sealed class Captured(string value)
    {
        public string Value { get; } = value;
    }
}
