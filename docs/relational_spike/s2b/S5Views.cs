// #536 spike S5 (PRD D32): one neutral view declaration, rendered by EF (an SQL view whose SQL EF writes), by
// LINQ to objects (InMemory) and by RavenDB (a static index). Plus the migrations half: a differ that writes
// CREATE VIEW / DROP VIEW into the scaffolded migration. Throwaway.
using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
using Microsoft.EntityFrameworkCore.Update.Internal;
using Raven.Client.Documents.Indexes;

namespace S2b;

/// <summary>
/// What a view reads. <see cref="Set{T}"/> is a collection; <see cref="Load{T}"/> follows a reference id to its row, or
/// null. Load is the one join form: RavenDB has it natively (<c>LoadDocument</c>), SQL makes it a scalar subquery
/// over the key, and LINQ to objects a lookup. A general <c>join</c> has no RavenDB map form.
/// </summary>
public interface ISparkViewSource
{
    IQueryable<T> Set<T>() where T : class;
    T? Load<T>(string? id) where T : class;
}

public interface ISparkView
{
    string Name { get; }
    Type ResultType { get; }
    LambdaExpression Definition { get; }

    /// <summary>The entity types the view reads (Set and Load), found in the declaration: the tables it depends on.</summary>
    IReadOnlyList<Type> ReadTypes { get; }
}

/// <summary>The declaration a generator (or a hand-written index) would emit, once, for every provider.</summary>
public abstract class SparkView<TResult> : ISparkView where TResult : class
{
    public virtual string Name => GetType().Name;
    public Type ResultType => typeof(TResult);
    public abstract Expression<Func<ISparkViewSource, IQueryable<TResult>>> Map { get; }

    // An expression-bodied property builds a NEW tree (new parameter instances) on every read: read it once.
    private Expression<Func<ISparkViewSource, IQueryable<TResult>>>? declaration;
    public Expression<Func<ISparkViewSource, IQueryable<TResult>>> Declaration => declaration ??= Map;
    LambdaExpression ISparkView.Definition => Declaration;

    public IReadOnlyList<Type> ReadTypes
    {
        get
        {
            var finder = new SourceCallFinder();
            finder.Visit(Declaration.Body);
            return [.. finder.Types.Distinct()];
        }
    }

    private sealed class SourceCallFinder : ExpressionVisitor
    {
        public List<Type> Types { get; } = [];
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType == typeof(ISparkViewSource)) Types.Add(node.Method.GetGenericArguments()[0]);
            return base.VisitMethodCall(node);
        }
    }
}

/// <summary>The row type the view yields: <c>VEmployee</c>, like HR's <c>VPerson</c>.</summary>
public class VEmployee
{
    public string? Id { get; set; }
    public string FullName { get; set; } = "";
    public string Owner { get; set; } = "";
    public string? CompanyName { get; set; }
    public int SkillCount { get; set; }
}

/// <summary>
/// The view S5 proves: a computed column (FullName, here NOT the O13 stored column), a value read through a reference
/// (CompanyName, the join D32 says needs a view), and a count over a D26 join table.
/// </summary>
public sealed class EmployeesWithCompany : SparkView<VEmployee>
{
    public override Expression<Func<ISparkViewSource, IQueryable<VEmployee>>> Map => s =>
        from e in s.Set<Employee>()
        select new VEmployee
        {
            Id = e.Id,
            FullName = e.FirstName + " " + e.LastName,
            Owner = e.Owner,
            CompanyName = s.Load<Company>(e.Company)!.Name,
            SkillCount = e.Skills.Count(),
        };
}

/// <summary>The same view with a changed definition (the migration must drop and recreate it).</summary>
public sealed class EmployeesWithCompanyV2 : SparkView<VEmployee>
{
    public override string Name => nameof(EmployeesWithCompany);
    public override Expression<Func<ISparkViewSource, IQueryable<VEmployee>>> Map => s =>
        from e in s.Set<Employee>()
        select new VEmployee
        {
            Id = e.Id,
            FullName = e.LastName + ", " + e.FirstName,
            Owner = e.Owner,
            CompanyName = s.Load<Company>(e.Company)!.Name,
            SkillCount = e.Skills.Count(),
        };
}

/// <summary>Replaces the source's calls in a declaration; each renderer says what Set and Load become.</summary>
public abstract class SourceRewriter(ParameterExpression source) : ExpressionVisitor
{
    protected abstract Expression RenderSet(Type type);
    protected abstract Expression RenderLoad(Type type, Expression id);

    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        if (node.Object == source && node.Method.Name == nameof(ISparkViewSource.Set))
            return RenderSet(node.Method.GetGenericArguments()[0]);
        if (node.Object == source && node.Method.Name == nameof(ISparkViewSource.Load))
            return RenderLoad(node.Method.GetGenericArguments()[0], Visit(node.Arguments[0]));
        return base.VisitMethodCall(node);
    }
}

/// <summary>EF: Set is the table's query root, Load a FirstOrDefault over the key (a scalar subquery per column read).</summary>
public static class EfViewRenderer
{
    public static IQueryable Render(ISparkView view, SparkEfContext db)
    {
        var body = new Rewriter(view.Definition.Parameters[0], db).Visit(view.Definition.Body);
        body = new JoinRewriter(db).Visit(body); // e.Skills.Count() over the D26 join table
        return Root(db, view.ReadTypes[0]).Provider.CreateQuery(body);
    }

    private static IQueryable Root(DbContext db, Type type)
        => (IQueryable)typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!.MakeGenericMethod(type).Invoke(db, [])!;

    private sealed class Rewriter(ParameterExpression source, DbContext db) : SourceRewriter(source)
    {
        protected override Expression RenderSet(Type type) => Root(db, type).Expression;

        protected override Expression RenderLoad(Type type, Expression id)
        {
            var t = Expression.Parameter(type, "t");
            var match = Expression.Lambda(Expression.Equal(Expression.Property(t, "Id"), id), t);
            var where = Expression.Call(typeof(Queryable), nameof(Queryable.Where), [type], RenderSet(type), Expression.Quote(match));
            return Expression.Call(typeof(Queryable), nameof(Queryable.FirstOrDefault), [type], where);
        }
    }
}

/// <summary>InMemory: LINQ to objects over the stored rows. Member access through Load is made null-safe, as SQL and RavenDB are.</summary>
public static class InMemoryViewRenderer
{
    public static IReadOnlyList<T> Run<T>(SparkView<T> view, IReadOnlyDictionary<Type, IList> rows) where T : class
    {
        var body = new Rewriter(view.Declaration.Parameters[0], rows).Visit(view.Declaration.Body);
        return Expression.Lambda<Func<IQueryable<T>>>(body).Compile()().ToList();
    }

    private sealed class Rewriter(ParameterExpression source, IReadOnlyDictionary<Type, IList> rows) : SourceRewriter(source)
    {
        protected override Expression RenderSet(Type type)
            => Expression.Constant(Queryable.AsQueryable(rows[type]), typeof(IQueryable<>).MakeGenericType(type));

        protected override Expression RenderLoad(Type type, Expression id)
        {
            var find = typeof(Rewriter).GetMethod(nameof(Find), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(type);
            return Expression.Call(find, Expression.Constant(rows[type]), id);
        }

        private static T? Find<T>(IList list, string? id) where T : class
            => id is null ? null : list.Cast<T>().FirstOrDefault(x => (string?)typeof(T).GetProperty("Id")!.GetValue(x) == id);

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression is MethodCallExpression { Method.Name: nameof(ISparkViewSource.Load) } call && call.Object == source)
            {
                var loaded = Visit(call);
                return Expression.Condition(Expression.Equal(loaded, Expression.Constant(null, loaded.Type)),
                    Expression.Default(node.Type), Expression.MakeMemberAccess(loaded, node.Member));
            }
            return base.VisitMember(node);
        }
    }
}

/// <summary>
/// RavenDB: the declaration must be <c>Set&lt;T&gt;().Select(...)</c>, which becomes <c>Map = docs =&gt; docs.Select(...)</c>;
/// Load becomes <c>LoadDocument</c>. The Id binding is dropped: RavenDB gives a projection its document id.
/// </summary>
public static class RavenViewRenderer
{
    public static AbstractIndexCreationTask Render<TEntity, TResult>(SparkView<TResult> view) where TResult : class
    {
        var index = new RavenView<TEntity>(view.Name);
        if (view.Declaration.Body is not MethodCallExpression { Method.Name: nameof(Queryable.Select) } select
            || select.Arguments[0] is not MethodCallExpression { Method.Name: nameof(ISparkViewSource.Set) })
            throw new NotSupportedException("A RavenDB map needs the shape Set<T>().Select(...).");
        var lambda = (LambdaExpression)((UnaryExpression)select.Arguments[1]).Operand;
        var rewritten = (LambdaExpression)new Rewriter(view.Declaration.Parameters[0], index).Visit(lambda);
        var docs = Expression.Parameter(typeof(IEnumerable<TEntity>), "docs");
        var call = Expression.Call(typeof(Enumerable), nameof(Enumerable.Select), [typeof(TEntity), typeof(TResult)], docs, rewritten);
        index.SetMap(Expression.Lambda<Func<IEnumerable<TEntity>, IEnumerable>>(call, docs));
        return index;
    }

    private sealed class Rewriter(ParameterExpression source, AbstractIndexCreationTask index) : SourceRewriter(source)
    {
        protected override Expression RenderSet(Type type) => throw new NotSupportedException("Set inside a RavenDB map.");

        protected override Expression RenderLoad(Type type, Expression id)
        {
            var load = index.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Single(m => m.Name == "LoadDocument" && m.IsGenericMethod && m.GetParameters() is [{ ParameterType: var p }] && p == typeof(string))
                .MakeGenericMethod(type);
            return Expression.Call(Expression.Constant(index), load, id);
        }

        protected override Expression VisitMemberInit(MemberInitExpression node)
            => Expression.MemberInit(node.NewExpression,
                node.Bindings.Where(b => b.Member.Name != "Id").Select(b => b is MemberAssignment a ? Expression.Bind(a.Member, Visit(a.Expression)) : b));
    }

    public sealed class RavenView<T> : AbstractIndexCreationTask<T>
    {
        private readonly string name;
        public RavenView(string name) => this.name = name;
        public override string IndexName => name;
        public void SetMap(Expression<Func<IEnumerable<T>, IEnumerable>> map)
        {
            Map = map;
            StoreAllFields(FieldStorage.Yes);
        }
    }
}

/// <summary>
/// D32's migrations half. EF's differ ignores a ToView entity, so this one adds the view statements around EF's own
/// operations: a view is dropped first and created last when its SQL changed, it appeared or disappeared, or EF
/// changes a table it reads (PostgreSQL refuses to alter a column a view depends on; SQL Server keeps stale view
/// metadata). ⚠️ Subclasses EF's pubternal MigrationsModelDiffer (EF1001): the provider package pins its EF version.
/// </summary>
#pragma warning disable EF1001
public sealed class SparkViewDiffer(
    IRelationalTypeMappingSource typeMappingSource,
    IMigrationsAnnotationProvider migrationsAnnotationProvider,
    IRelationalAnnotationProvider relationalAnnotationProvider,
    IRowIdentityMapFactory rowIdentityMapFactory,
    CommandBatchPreparerDependencies commandBatchPreparerDependencies,
    ISqlGenerationHelper sql)
    // ⚠️ EF main already adds an IDiagnosticsLogger parameter to this constructor (after v11.0.0): pubternal churn.
    : Microsoft.EntityFrameworkCore.Migrations.Internal.MigrationsModelDiffer(typeMappingSource, migrationsAnnotationProvider,
        relationalAnnotationProvider, rowIdentityMapFactory, commandBatchPreparerDependencies)
{
    public const string SqlAnnotation = "Spark:ViewSql";
    public const string TablesAnnotation = "Spark:ViewTables";

    public override bool HasDifferences(IRelationalModel? source, IRelationalModel? target) => GetDifferences(source, target).Count > 0;

    public override IReadOnlyList<MigrationOperation> GetDifferences(IRelationalModel? source, IRelationalModel? target)
    {
        var operations = base.GetDifferences(source, target);
        var before = Views(source);
        var after = Views(target);
        var touched = operations.OfType<ITableMigrationOperation>().Select(o => o.Table)
            .Concat(operations.OfType<RenameTableOperation>().Select(o => o.NewName ?? o.Name)).ToHashSet();

        bool Changed(string name, (string Sql, string[] Tables) v, Dictionary<string, (string Sql, string[] Tables)> other)
            => !other.TryGetValue(name, out var o) || o.Sql != v.Sql || v.Tables.Any(touched.Contains) || o.Tables.Any(touched.Contains);

        var drops = before.Where(v => Changed(v.Key, v.Value, after))
            .Select(v => new SqlOperation { Sql = $"DROP VIEW {sql.DelimitIdentifier(v.Key)};" });
        var creates = after.Where(v => Changed(v.Key, v.Value, before))
            .Select(v => new SqlOperation { Sql = $"CREATE VIEW {sql.DelimitIdentifier(v.Key)} AS\n{v.Value.Sql};" });
        return [.. drops, .. operations, .. creates];
    }

    private static Dictionary<string, (string Sql, string[] Tables)> Views(IRelationalModel? model)
        => model?.Model.GetEntityTypes()
            .Where(e => e.FindAnnotation(SqlAnnotation)?.Value is string)
            .ToDictionary(e => e.GetViewName()!, e => ((string)e[SqlAnnotation]!, ((string?)e[TablesAnnotation] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)))
           ?? [];
}
#pragma warning restore EF1001
