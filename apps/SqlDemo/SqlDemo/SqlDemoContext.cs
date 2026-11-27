using MintPlayer.Spark;
using SqlDemo.Entities;

namespace SqlDemo;

/// <summary>
/// The demo's two tables. <c>Session</c> is Spark's store-neutral session (#551), so nothing here names PostgreSQL:
/// the provider is chosen once, in <c>Program.cs</c>.
/// </summary>
public partial class SqlDemoContext : SparkContext
{
    public IQueryable<Author> Authors => Session.Query<Author>();
    public IQueryable<Book> Books => Session.Query<Book>();
}
