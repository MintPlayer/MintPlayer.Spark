# MintPlayer.Spark.Templates

`dotnet new` templates for [MintPlayer.Spark](https://github.com/MintPlayer/MintPlayer.Spark).

```
dotnet new install MintPlayer.Spark.Templates
dotnet new spark-app -n MyApp
cd MyApp
dotnet run --project MyApp
```

`dotnet run` is the whole command: the first build runs `npm install` in `MyApp/ClientApp`, and in Development the
host starts the Angular dev server itself and proxies it. Node.js must be installed (not with `--no-client`).

## `spark-app`

A Spark host, an entity library and an Angular client. The template pins every Spark package in
`Directory.Packages.props`, at the versions released with it.

The solution file follows your tools. `dotnet new` writes `MyApp.slnx`. Visual Studio, Rider and C# Dev Kit create
the solution themselves, in the format their own settings choose (`.sln` or `.slnx`).

| Option | Values | Default | Notes |
|---|---|---|---|
| `--store` | `RavenDB`, `InMemory` | `RavenDB` | `--auth` and the options marked RavenDB need it. |
| `--auth` | `none`, `authorization`, `oidc` | `none` | RavenDB. Accounts and security.json groups; `oidc` adds an external provider. |
| `--oidc-authority` | URL | | Required with `--auth oidc`. |
| `--no-client` | | off | The API host only. |
| `--controllers`, `--logging` | | off | Any store. |
| `--migrations` | | on | RavenDB. Includes a sample seed migration. |
| `--messaging`, `--mail`, `--cron`, `--history`, `--soft-delete`, `--mcp` | | off | RavenDB. `--mail` turns on messaging. |

A RavenDB-only option has no effect with `--store InMemory`.

## Item templates

Run each one in the project directory it belongs to. It writes one file into that project's conventional folder, in
the namespace `<RootNamespace>.<Folder>` (`--namespace` overrides the root).

| Template | Run it in | Writes |
|---|---|---|
| `dotnet new spark-entity -n Invoice` | `MyApp.Library` | `Entities/Invoice.cs` |
| `dotnet new spark-lookup -n InvoiceStatus` | `MyApp.Library` | `LookupReferences/InvoiceStatus.cs` |
| `dotnet new spark-actions -n Invoice` | `MyApp` | `Actions/InvoiceActions.cs` |
| `dotnet new spark-custom-action -n Approve --entity Invoice` | `MyApp` | `CustomActions/ApproveAction.cs` |
| `dotnet new spark-index -n Invoice` | `MyApp` (RavenDB) | `Indexes/Invoices_Overview.cs` |

An item edits no JSON file. The doc comment of the file it writes lists what else is needed (the context property,
the `actions.json` entry, the `security.json` right, `--spark-synchronize-model`), with your names filled in.

## Testing the template

The template is tested locally, not in CI. Run this from the repository root before you merge a change to the
template, `features.json`, or a library the template references:

```
node tools/template-matrix.mjs
```

It packs every package the template pins from the current commit into a local feed, then generates every variant
(minimal, defaults, OIDC, all features on, and each feature alone). Each variant is restored from that feed only and
built, then run with `--spark-synchronize-model` and `--spark-verify-model`. The defaults variant also gets every item
template. Each distinct Angular client is built once, against one shared `node_modules`. It takes about 11 minutes,
and needs no RavenDB server.

| Option | Effect |
|---|---|
| `--only Defaults,AllOn` | Only these variants (names are printed in the summary). |
| `--no-client` | Skip the Angular builds. |
| `--ng-spark <tgz>` | Build the clients against an `npm pack` of the local `@mintplayer/ng-spark` instead of the released one. |
| `--parallel <n>` | Variants built at once (default 4). |
| `--out <dir>` | Where the variants and logs go (default: `spark-template-matrix` in the temp folder). |

A failing step names its log in `<out>/logs/`.
