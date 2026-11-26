# spark and create-spark-app

Two .NET tools around the `MintPlayer.Spark.Templates` pack. Both run the same commands; neither writes an app's
files itself: they instantiate the templates, then make the edits the templates' doc comments describe.

## Create an app

```sh
dnx create-spark-app            # asks for the name, store, sign-in, client and features
dnx MintPlayer.Spark.Cli        # the same, outside a Spark app
dnx create-spark-app Shop --yes # no questions: the template's defaults
```

Or install the tool once: `dotnet tool install -g MintPlayer.Spark.Cli`, then `spark new`.

| Option | |
|---|---|
| `[name]` | The solution, app project and namespace. |
| `-o`, `--output` | The directory to create; defaults to the name. |
| `--store RavenDB\|InMemory` | Sign-in, Replication and the OpenSearch engine need RavenDB. The SQL Server and PostgreSQL stores are not offered yet: add them to the generated app by hand (`UseSqlServer`/`UsePostgreSql`, as `apps/SqlDemo` does). |
| `--auth none\|authorization\|oidc` | Local accounts, or an external provider with `--oidc-authority <url>`. |
| `--features a,b` | Feature flags (`migrations,history,…`) or `none`; prerequisites are added. |
| `--no-client` | No Angular client. |
| `-y`, `--yes`, `--no-interactive` | Ask nothing. On CI and with piped input nothing is asked anyway. |
| `--skip-npm`, `--skip-sync` | Skip `npm install` in ClientApp, or the first model synchronization. |

Before generating, `new` refuses combinations the template cannot build (a RavenDB feature on InMemory, sign-in
without RavenDB, a client feature with `--no-client`), checks that RavenDB answers at `http://localhost:8080` (and
offers InMemory when it does not), and checks the OIDC authority's discovery document. Afterwards it runs
`npm install` and `--spark-synchronize-model`, so the first `dotnet run` starts a working app.

The tool installs the `MintPlayer.Spark.Templates` version released with it (`dotnet new install … --force`) when
another version, or none, is installed.

## Add to an app

Run in the app, its entity library or the solution directory:

```sh
spark add entity Invoice                          # Entities/Invoice.cs, the context root, a security right
spark add actions Invoice                         # Actions/InvoiceActions.cs
spark add custom-action Approve --entity Invoice  # the class, its actions.json entry and its right
spark add lookup InvoiceStatus                    # LookupReferences/InvoiceStatus.cs
spark add index Invoice                           # Indexes/Invoices_Overview.cs and its view
```

Rights go to the `Administrators` group unless `--group` names another (a group name, its id, `@authenticated` or
`@anonymous`). `entity`, `lookup`, `custom-action` and `index` end with `--spark-synchronize-model` unless
`--skip-sync` is given.
