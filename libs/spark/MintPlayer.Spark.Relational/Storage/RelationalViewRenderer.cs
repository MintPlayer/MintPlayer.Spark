using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using MintPlayer.Spark.Abstractions.Views;
using MintPlayer.Spark.Relational.Model;

namespace MintPlayer.Spark.Relational.Storage;

/// <summary>
/// A <see cref="SparkView{TRow}"/> as SQL that EF writes (#536 D32, S5): <c>Set&lt;T&gt;()</c> is the table's query
/// root, <c>Load&lt;T&gt;(id)</c> a <c>FirstOrDefault</c> over the key (a scalar subquery per column read, NULL for a
/// missing reference), <c>IdOf(row)</c> the key column, and a reference list a subquery over its join table (D26).
/// </summary>
internal static class RelationalViewRenderer
{
    /// <summary>The SQL of every view in the model, rendered on <paramref name="db"/>, a context whose views carry none yet.</summary>
    public static Dictionary<string, SparkViewSql> RenderAll(SparkDbContext db)
        => db.SparkModel.Views.ToDictionary(v => v.Name, v => new SparkViewSql(Sql(v, db), TablesRead(v, db)), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The view's SELECT, with <c>\n</c> line ends so the annotation is the same on every machine. A view body takes no
    /// parameters, so a declaration that captures a variable is refused.
    /// </summary>
    public static string Sql(ISparkView view, SparkDbContext db)
    {
        string sql;
        try
        {
            sql = Query(view, db).ToQueryString();
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"View {view.Name} cannot be written as SQL: {ex.Message}", ex);
        }

        sql = sql.Replace("\r\n", "\n").Trim();
        if (!sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"View {view.Name} uses a value that is not a constant (EF made it a query parameter). A view's Map may only use constants:\n{sql}");
        return sql;
    }

    /// <summary>The declaration as an EF query over <paramref name="db"/>.</summary>
    public static IQueryable Query(ISparkView view, SparkDbContext db)
    {
        var body = new Rewriter(view.Definition, db).Visit(view.Definition.Body);
        body = KeyBesideRow(body, view);
        if (view.ReduceDefinition is { } reduce)
            body = ReduceRewriter.Over(reduce, body);
        var root = Set(db, view.CollectionType);
        var query = root.Provider.CreateQuery(body);
        var rewrite = typeof(JoinRewriter).GetMethod(nameof(JoinRewriter.Rewrite))!.MakeGenericMethod(query.ElementType);
        return (IQueryable)rewrite.Invoke(null, [db, query])!;
    }

    /// <summary>
    /// <c>IdOf(row)</c> is the key column, whose CLR type (an <c>Id&lt;T&gt;</c>, a Guid) is not the row's <c>Id</c>
    /// type, and EF has no cast between them. So the key is projected beside the row:
    /// <c>Select(p =&gt; new SparkViewRow { Id = p.Id, Row = new TRow { … } })</c>, which EF flattens to the same columns,
    /// the key named <c>Id</c>. The view's row type maps that column with the collection's key converter.
    /// </summary>
    private static Expression KeyBesideRow(Expression body, ISparkView view)
    {
        if (body is not MethodCallExpression { Method.Name: nameof(Queryable.Select), Arguments: [var source, UnaryExpression { Operand: LambdaExpression selector }] } select
            || select.Method.DeclaringType != typeof(Queryable)
            || selector.Body is not MemberInitExpression init)
        {
            KeyMarkerFinder.Refuse(body, view);
            return body;
        }

        var keyBinding = init.Bindings.OfType<MemberAssignment>().FirstOrDefault(b => KeyMarkerFinder.IsMarker(b.Expression));
        if (keyBinding is null || keyBinding.Member.Name != "Id")
        {
            KeyMarkerFinder.Refuse(body, view);
            return body;
        }

        var rest = init.Bindings.Where(b => b != keyBinding).ToList();
        var row = Expression.MemberInit(init.NewExpression, rest);
        KeyMarkerFinder.Refuse(row, view);
        var key = ((MethodCallExpression)keyBinding.Expression).Arguments[0];
        var wrapper = typeof(SparkViewRow<,>).MakeGenericType(view.RowType, key.Type);
        var projected = Expression.MemberInit(Expression.New(wrapper),
            Expression.Bind(wrapper.GetProperty(nameof(SparkViewRow<object, object>.Id))!, key),
            Expression.Bind(wrapper.GetProperty(nameof(SparkViewRow<object, object>.Row))!, row));
        var lambda = Expression.Lambda(projected, selector.Parameters);
        return Expression.Call(typeof(Queryable), nameof(Queryable.Select), [selector.Parameters[0].Type, wrapper], source, Expression.Quote(lambda));
    }

    /// <summary><c>IdOf(row)</c> while the declaration is rewritten; only ever the row's <c>Id</c> binding.</summary>
    private static string? KeyOf<TKey>(TKey key) => throw new NotSupportedException("A marker; never called.");

    private sealed class KeyMarkerFinder : ExpressionVisitor
    {
        private bool found;

        public static bool IsMarker(Expression e) => e is MethodCallExpression { Method: { IsGenericMethod: true } m } && m.GetGenericMethodDefinition() == KeyOfT;

        public static void Refuse(Expression e, ISparkView view)
        {
            var finder = new KeyMarkerFinder();
            finder.Visit(e);
            if (finder.found)
                throw new NotSupportedException(
                    $"View {view.Name}: IdOf(row) may only bind the row's Id in the view's Select (Set<T>().Select(x => new {view.RowType.Name} {{ Id = s.IdOf(x), … }})).");
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            found |= IsMarker(node);
            return base.VisitMethodCall(node);
        }
    }

    private static readonly MethodInfo KeyOfT = typeof(RelationalViewRenderer).GetMethod(nameof(KeyOf), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>
    /// The tables a view depends on: each read type's table, its child tables (D25) and its join tables (D26), sorted.
    /// A migration that changes one of them drops and recreates the view.
    /// </summary>
    private static List<string> TablesRead(ISparkView view, SparkDbContext db)
    {
        var tables = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var table in view.ReadTypes.Select(db.SparkModel.RequireTable).Distinct())
        {
            var types = table.Derived.Prepend(table.Root).ToHashSet();
            foreach (var entityType in db.Model.GetEntityTypes())
            {
                var owner = entityType.FindOwnership()?.PrincipalEntityType.ClrType ?? entityType.ClrType;
                if (types.Contains(owner) && entityType.GetTableName() is { } name)
                    tables.Add(name);
            }
            foreach (var join in db.SparkModel.Joins.Where(j => db.SparkModel.TableOf(j.Owner) == table))
                tables.Add(join.Name);
        }
        return [.. tables];
    }

    private static IQueryable Set(SparkDbContext db, Type type)
    {
        db.SparkModel.RequireTable(type);
        return (IQueryable)SetOfT.MakeGenericMethod(type).Invoke(db, [])!;
    }

    private static readonly MethodInfo SetOfT = typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!;


    /// <summary>
    /// S16: a view's reduce over the map's query. The reduce is written against <see cref="IEnumerable{T}"/> (RavenDB's
    /// form); its parameter becomes the map query and each <see cref="Enumerable"/> call on a query becomes the
    /// <see cref="Queryable"/> one, so EF writes <c>GROUP BY</c> with <c>SUM</c>. Calls on a group (<c>g.Sum</c>) stay.
    /// </summary>
    private sealed class ReduceRewriter(ParameterExpression rows, Expression map) : ExpressionVisitor
    {
        public static Expression Over(LambdaExpression reduce, Expression map)
            => new ReduceRewriter(reduce.Parameters[0], map).Visit(reduce.Body);

        protected override Expression VisitParameter(ParameterExpression node) => node == rows ? map : node;

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var args = node.Arguments.Select(Visit).ToList();
            if (node.Method.DeclaringType != typeof(Enumerable) || args.Count == 0 || !typeof(IQueryable).IsAssignableFrom(args[0]!.Type))
                return node.Update(node.Object, args!);

            var generic = node.Method.IsGenericMethod ? node.Method.GetGenericArguments() : Type.EmptyTypes;
            foreach (var candidate in typeof(Queryable).GetMethods().Where(m => m.Name == node.Method.Name
                         && m.GetParameters().Length == args.Count && m.IsGenericMethod == node.Method.IsGenericMethod
                         && (!m.IsGenericMethod || m.GetGenericArguments().Length == generic.Length)))
            {
                var method = candidate.IsGenericMethod ? candidate.MakeGenericMethod(generic) : candidate;
                var parameters = method.GetParameters();
                var call = new Expression[args.Count];
                var fits = true;
                for (var i = 0; i < args.Count && fits; i++)
                {
                    var arg = args[i]!;
                    if (arg is LambdaExpression lambda)
                    {
                        fits = parameters[i].ParameterType == typeof(Expression<>).MakeGenericType(lambda.Type);
                        call[i] = Expression.Quote(lambda);
                    }
                    else
                    {
                        fits = parameters[i].ParameterType.IsAssignableFrom(arg.Type);
                        call[i] = arg;
                    }
                }
                if (fits)
                    return Expression.Call(method, call);
            }

            throw new NotSupportedException($"A view's reduce cannot call Enumerable.{node.Method.Name} on the map's rows in SQL.");
        }
    }

    private sealed class Rewriter(LambdaExpression definition, SparkDbContext db) : SparkViewRewriter(definition)
    {
        protected override Expression RenderSet(Type type) => Set(db, type).Expression;

        protected override Expression RenderLoad(Type type, Expression id)
        {
            var table = db.SparkModel.RequireTable(type);
            var keyType = table.IdProperty.PropertyType;
            var idType = Nullable.GetUnderlyingType(id.Type) ?? id.Type;
            if (idType != keyType)
                throw new NotSupportedException(
                    $"Load<{type.Name}>: the id is a {id.Type.Name} and {type.Name}'s key a {keyType.Name}. On the relational providers a view loads by the key's own type; declare the reference as {keyType.Name}.");

            var t = Expression.Parameter(type, "t");
            Expression key = Expression.Property(Expression.Convert(t, table.Root), table.IdProperty);
            if (key.Type != id.Type)
                key = Expression.Convert(key, id.Type);
            var where = Expression.Call(typeof(Queryable), nameof(Queryable.Where), [type], RenderSet(type),
                Expression.Quote(Expression.Lambda(Expression.Equal(key, id), t)));
            return Expression.Call(typeof(Queryable), nameof(Queryable.FirstOrDefault), [type], where);
        }

        /// <summary>A marker over the key column, which <see cref="KeyBesideRow"/> moves beside the row.</summary>
        protected override Expression RenderIdOf(Expression row)
        {
            var table = db.SparkModel.RequireTable(row.Type);
            var key = Expression.Property(row.Type == table.Root ? row : Expression.Convert(row, table.Root), table.IdProperty);
            return Expression.Call(KeyOfT.MakeGenericMethod(key.Type), key);
        }
    }
}

/// <summary>A view row and its key, side by side: what EF renders a view's SELECT from (see <c>KeyBesideRow</c>).</summary>
internal sealed class SparkViewRow<TRow, TKey>
{
    public TKey Id { get; set; } = default!;

    public TRow Row { get; set; } = default!;
}
