# SQL Server and PostgreSQL

Spark runs on RavenDB, on **SQL Server** and on **PostgreSQL**. The store is one line in `Program.cs`; the model,
`security.json`, the menu, the translations, the actions and the feature packages stay the same. This guide covers
what changes with a SQL store: the schema and its migrations, views in place of indexes, ids, and testing.

`apps/SqlDemo` is the running example: a book catalogue on PostgreSQL, with Messaging, MailManager, History and
local accounts ([its README](../apps/SqlDemo/README.md)). The design and its decisions are in
[`relational_providers_PRD.md`](relational_providers_PRD.md).

## Choosing the store

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UsePostgreSql();          // MintPlayer.Spark.PostgreSql, PostgreSQL 17 or later
    // spark.UseSqlServer();        // MintPlayer.Spark.SqlServer, SQL Server 2022 or later
    spark.UseContext<AppContext>();
});
```

The connection string is `ConnectionStrings:Spark`. The options are:

| Option | What it does |
|---|---|
| `ConnectionString` | instead of `ConnectionStrings:Spark` |
| `MigrationsAssembly` | where the app's migrations live; the entry assembly by default |
| `ConfigureModel(b => …)` | adjusts the EF model Spark builds (an index, a column type); runs last, and the next migration follows it |
| `ConfigureContext(o => …)` | logging, interceptors on the EF context |
| `ChangeFeedPollInterval` | how often live updates and subscriptions poll for committed changes |

A feature that needs something the store doesn't offer is refused at compile time (SPARK060). Replication is
RavenDB-to-RavenDB, so it is the one feature a SQL store can't have.

## A table per stored type

Every stored type gets a table: the types in the app model (`App_Data/Model/*.json`) and the ones feature packages
declare. A type's properties are columns. Value objects and collections are JSON columns (`nvarchar(max)` on SQL
Server, `jsonb` on PostgreSQL). Every table also has Spark's own columns: `Version` for optimistic concurrency (a stale
save is still a 409), the change stamp that live updates and subscriptions read, and `SparkExpiresAt`.

Feature packages keep their state in tables too: users and roles, messages, mail deliveries, revisions, migration
markers. The framework's own tables (atomic values, reservations, revisions, tombstones, data-protection keys) are in
the `spark` schema, under their own migration history.

## Ids and references

A stored type's key is `Id<T>`, a uuid by default:

```csharp
public partial class Author
{
    public Id<Author> Id { get; set; }
}

[IdKind(SparkIdKind.Integer)]   // or Natural: a key the code chooses
public partial class Invoice { public Id<Invoice> Id { get; set; } }
```

Declare a reference as a typed id, `Id<Author>?`. The column then gets a real foreign key, and a view can join on it.
A `[Reference]` `string` is stored in the same column, but a view refuses to join on it: "the id is a String and
Author's key a Id`1". A RavenDB app's string references need a typed id before their joins can move to SQL.

## Schema changes are migrations

The schema is an EF Core migration, written by the command you already run after a model change:

```bash
dotnet run --no-launch-profile -- --spark-synchronize-model
```

It updates `App_Data/Model/*.json` and, when the schema changed, writes the next migration into
`Migrations/<Provider>` (`PostgreSql` or `SqlServer`) in the host project. Review the migration and commit it with the
model. There is no `dotnet ef` and no design-time factory. The migration writer is
`MintPlayer.Spark.Relational.Design`, a development dependency, so it never ships in a publish.

Synchronize refuses two migrations that would lose data, and writes nothing:

- **a table the last snapshot already has, created again:** EF compared against no previous model;
- **a column dropped and a column added for the same attribute id:** a renamed attribute. If it really is a new
  attribute, give it a new id in the model.

### Applying them

At startup, before it serves a request, the app applies the framework schema, then its own migrations, then the data
migrations (`ISparkMigration`). It creates the database first if it doesn't exist. Several instances may start at
once: one applies the migrations and the others wait for it.

If the model has changes that no migration covers, the app refuses to start and names the tables and columns. Run
synchronize on a development machine and commit the migration it writes.

For a deployment that migrates separately, set `Spark:Database:ApplyMigrations` to `false`. The app then runs with a
login without DDL rights, and refuses to start while a migration is pending. Apply them with one of these commands:

```bash
dotnet App.dll --spark-migrate                       # schema and data migrations, then exits
dotnet App.dll --spark-migration-script schema.sql   # an idempotent script for a DBA
               [--from <migration>] [--to <migration>]
```

`--spark-help` lists every command.

## Views are the indexes

A RavenDB static index becomes an SQL view. It is declared once, in C#, and works on every store: RavenDB deploys it
as an index, and on SQL synchronize writes it into the migration as a `CREATE VIEW`.

```csharp
[DefaultIndex]   // the Book list's source: Book.json's queryType and indexName come from it
public sealed class Books_WithAuthor : SparkView<VBook>
{
    public override Expression<Func<ISparkViewSource, IQueryable<VBook>>> Map => s =>
        from book in s.Set<Book>()
        select new VBook
        {
            Id = s.IdOf(book),
            Title = book.Title,
            Author = book.Author,
            AuthorName = s.Load<Author>(book.Author)!.Name,   // a left join: no author reads as null
            Year = book.Year,
        };
}
```

- `s.Set<T>()` is the source table, `s.IdOf(x)` its key, and `s.Load<T>(id)` the one join form.
- A view is computed when it is read, so it is never stale: a saved row is in the list at once.
- Query a view, never a plain collection, wherever RavenDB would need an index. On RavenDB a query without one
  creates an auto index, which Spark forbids.

Reduce, multi-map and fan-out views (`SelectMany`) are SQL views too, with `GROUP BY`, `UNION ALL` and a lateral join.

## What each store offers

| | RavenDB | SQL Server | PostgreSQL |
|---|---|---|---|
| Live updates, subscriptions, Messaging | changes API, subscriptions | the change stamp, polled; deletes as tombstones | the same |
| Revisions (History) | revisions | a revisions table | a revisions table |
| Expiry | `@expires` | `SparkExpiresAt`, swept in process | the same |
| Search | `Search()` | `LIKE`, escaped | `LIKE`, escaped |
| Attachments | yes | off: Files uses its blob stores | off |
| Replication | yes | refused (SPARK060) | refused |

## Testing

A test project picks its database with `SPARK_TEST_DATABASE`: `RavenDB`, `InMemory`, `SqlServer` or `PostgreSql`. The
SQL servers come from `MintPlayer.Spark.Testing.SqlServer` and `MintPlayer.Spark.Testing.PostgreSql`:

| Store | The server, in order |
|---|---|
| SQL Server | `SPARK_TEST_SQLSERVER` (a connection string without a database); LocalDB on Windows; a Testcontainers container |
| PostgreSQL | `SPARK_TEST_POSTGRESQL`; an embedded PostgreSQL 18 (instance `spark-tests`, no Docker needed), started once and reused |

Each test database is created on the server and dropped afterwards. In this repository, a provider-neutral test class
carries the trait `SparkDatabase=Any`, and CI runs those classes again on SQL Server 2025 and PostgreSQL 18.

An E2E host runs a PostgreSQL app the same way: `SparkAppDescriptor.Store = SparkAppStore.PostgreSql` gives the app
an empty database, and the app applies its own migrations at startup (`SqlDemoTestHost`).
