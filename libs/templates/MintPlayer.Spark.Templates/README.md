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
