# SqlDemo — Spark on PostgreSQL

The demo of the relational storage providers (#536, `docs/relational_providers_PRD.md`): a small book catalogue on
**PostgreSQL**, with the features the RavenDB demos use, unchanged. Only the store differs. Its E2E suite
(`tests/MintPlayer.Spark.E2E.Tests/SqlDemo/`) runs it end to end.

```
apps/SqlDemo/
├── SqlDemo.Library/   Author, Book (the stored types; Book is IAuditable for History)
└── SqlDemo/           the host: Program.cs, SqlDemoContext, Views/Books_WithAuthor.cs,
                       Migrations/PostgreSql, App_Data, ClientApp (Angular)
```

## Running it

Start a PostgreSQL 17 or later, then:

```bash
cd apps/SqlDemo/SqlDemo
dotnet run --launch-profile https      # https://localhost:5017
```

The `https` profile connects to the database `SparkSqlDemo` on `localhost:5432` as `postgres`, and Spark creates the
database at the first start. Neither the profile nor `appsettings.Development.json` holds a password, and none belongs in
the repository: Npgsql reads it from your pgpass file or from `PGPASSWORD`. Set one of them once per machine:

- **The pgpass file:** `%APPDATA%\postgresql\pgpass.conf` on Windows; `~/.pgpass` on Linux and macOS, where it must be
  `chmod 600` or it is ignored. Each line is `host:port:database:user:password`:

  ```
  localhost:5432:*:postgres:<your password>
  ```

- **`PGPASSWORD`:** `setx PGPASSWORD <your password>` on Windows, which reaches only processes started afterwards (restart
  Visual Studio or the terminal), or `export PGPASSWORD=<your password>` in your shell profile.

The profile sets `ConnectionStrings__Spark`, so a value from your shell does not override it. For another server or user,
change it in the profile, and keep the password out of it.

`dotnet run` is the whole command: the host starts the Angular dev server itself (`UseAngularCliServer`), so don't run
`ng serve` next to it. Mail is not sent: every message is an `.eml` file in `apps/SqlDemo/SqlDemo/mail-pickup/`
(the confirmation link of a new account is in there).

A fresh database has no administrator. Register, confirm the address through the link in `mail-pickup/`, then give the
account the `Administrators` group: add `{ "ClaimType": "group", "ClaimValue": "Administrators" }` to the `Claims`
column of its `SparkUsers` row.

## What the store changes, and what it doesn't

| Concern | RavenDB app | SqlDemo |
|---|---|---|
| Store | `spark.UseRavenDb()`, `Spark:RavenDb` | `spark.UsePostgreSql()`, `ConnectionStrings:Spark` |
| Schema | none: documents | tables, one per stored type, from EF migrations in `Migrations/PostgreSql` |
| Ids | `Books/1-A` | `Id<Book>`, a `uuid` (D23); a reference is a typed `Id<Author>?` with a real foreign key (D26) |
| A query's source | a RavenDB index | a SQL view: `Books_WithAuthor` is a `SparkView` (D32), and `[DefaultIndex]` makes it `GetBooks`' source |
| Security, model, menu, translations | `App_Data/*.json` | the same files, unchanged: they know no store |
| Messaging, MailManager | subscriptions | a message table, picked up through the change feed (D39) |
| History | RavenDB revisions | revision rows (S8) |

## Changing the model

Edit an entity or a view, then run

```bash
dotnet run --no-launch-profile -- --spark-synchronize-model
```

It updates `App_Data/Model/*.json` and, when the schema changed, writes the next migration into
`Migrations/PostgreSql` (the views' `CREATE VIEW` included). The app applies it at the next start; to apply or review it
first, use `--spark-migrate` or `--spark-migration-script`. `--spark-help` lists every command.

### A view joins on the key's own type

A view's `s.Load<Author>(book.Author)` must pass the id in the key's CLR type. With `Book.Author` declared as a plain
`string` holding an author's id, synchronize refuses the view: "Load<Author>: the id is a String and Author's key a
Id`1". Declare the reference as `Id<Author>?`. The column then also gets a foreign key.
