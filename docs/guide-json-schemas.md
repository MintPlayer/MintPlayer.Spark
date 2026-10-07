# JSON schemas for Spark's App_Data files

Spark publishes a JSON Schema for each of the six `App_Data` files you edit by hand, so an editor
(VS Code, Visual Studio, Rider) validates and completes them while you type — a typo, a property
Spark does not read, or a removed one such as `isVisible` is flagged before the host ever starts
(#264).

| File | Schema |
|---|---|
| `App_Data/Model/*.json` | `model.schema.json` |
| `App_Data/security.json` | `security.schema.json` |
| `App_Data/programUnits.json` | `programUnits.schema.json` |
| `App_Data/translations.json` | `translations.schema.json` |
| `App_Data/culture.json` | `culture.schema.json` |
| `App_Data/actions.json` | `actions.schema.json` |

Generated files (`modelHashes.json`, `oidc-signing-key.json`, `securityPosture.txt`) have none: you
never edit them.

**Hovering a key shows what it means.** Every property carries a `description` (`persistentObject`,
`clrType`, `showedOn`, a right's `key`, `bindings`, `$remove`, …), and an enum lists what each of its
values does. In a library's file the descriptions also say what a layer may state: a delta leaves
`id` out, a library may only grant, `groups` is the application's only.

---

## Where they live

```
https://schemas.spark.mintplayer.com/v{n}/<file>.schema.json
```

for example `https://schemas.spark.mintplayer.com/v1/model.schema.json`. The site sends
`Access-Control-Allow-Origin: *`, `Content-Type: application/schema+json` and immutable cache
headers: a published revision never changes.

**`v{n}` is one global revision for the whole set**, not the package version. It moves only when
any generated schema changes, so upgrading Spark without a schema change leaves your files alone.

- Each revision is a git tag `schemas/v{n}` in this repository and a **GitHub release** of the same
  name whose assets are the six files. The release is the archive; the site is rebuilt from the
  releases alone.
- CI applies revisions automatically: on every push to `master` it generates the set, compares it
  byte-for-byte with the assets of the latest `schemas/v*` release, and on any difference tags the
  commit `schemas/v{n+1}`, creates the release, deploys the site and compiles `n+1` into
  `MintPlayer.Spark`. Otherwise the package compiles in `n`. A package therefore never names a
  revision whose release does not exist.

## `$schema` is managed for you

`--spark-synchronize-model` keeps the root `"$schema"` of all six file kinds in line with the Spark
package you run:

- **missing** → it adds `https://schemas.spark.mintplayer.com/v{n}/<file>.schema.json`, with `n` the
  revision compiled into the installed package;
- **a `schemas.spark.mintplayer.com` URL** → it rewrites only the `v{n}` segment, so after an upgrade
  that changed the schemas your files point at the new revision;
- **anything else** (a relative path, a URL of your own) → left alone.

The edit is textual: only the `$schema` line changes, and formatting, key order and `_` comments
survive byte for byte. `$schema` and `_` properties never take part in the model hash or in what the
loaders read. A build that knows of no published revision (`n = 0`) writes nothing.

## Comments: the `_` convention

The schemas are **strict** (`additionalProperties: false`): a property Spark does not read is an
error in the editor, because the server silently ignores unknown properties and a typo would
otherwise just not work. To keep room for notes, every strict object also allows properties whose
name starts with `_` (`patternProperties: { "^_": {} }`):

```jsonc
{
  "$schema": "https://schemas.spark.mintplayer.com/v1/security.schema.json",
  "_comment": "Visitors may read a user's name and nothing else (QnA, G-Q15).",
  "wellKnown": { … }
}
```

In `translations.json` a comment must be a **string** (its reader refuses numbers and arrays there).
Flags enums written as strings (`"showedOn": "Query, PersistentObject"`) are checked against the
member names, and `"None"` is accepted.

## In this repository

The apps here do **not** use the hosted URL. Their files reference the **unversioned, locally
generated** schemas by relative path, so a pull request validates against the schemas of its own
branch rather than against whatever is deployed:

```json
{ "$schema": "../../../../../schemas/model.schema.json" }
```

(`apps/<App>/<App>/App_Data/Model/x.json` → repo root; one level less for the other five files.)
Synchronize leaves relative paths alone. ⚠️ A model file **copied from this repository** into another
application should have its `$schema` line removed; the next synchronize then adds the hosted URL.

The schemas are **build artifacts and are never committed**: `schemas/` at the repository root is in
`.gitignore`. ⚠️ A fresh clone therefore has no schemas until it is built once; until then every
relative `$schema` is unresolved and the editor validates nothing. After a build that changed the
schemas, VS Code may keep the old ones cached: run **Developer: Reload Window**.

### Regenerating them locally

`tools/SchemaGenerator` (`MintPlayer.Spark.SchemaGenerator`, part of the solution) writes the set to
`schemas/` after its own build, incrementally — any `dotnet build` of the solution keeps them current.
To regenerate on demand:

```
dotnet build tools/SchemaGenerator
```

The schemas are exported with `System.Text.Json`'s `JsonSchemaExporter` from the types the loaders
deserialize (`EntityTypeFile`, `ProgramUnitsConfiguration`, and mirrors of what the security, culture
and actions loaders read: `SecurityFile`, `CultureFile`, `ActionsFileEntry`), plus a hand-built tree
for `translations.json`, which no CLR type describes.

The descriptions are those types' `///` summaries. The projects holding them build with
`GenerateDocumentationFile` (CS1591 is suppressed: only schema-facing members need a summary), and
the generator reads the XML file next to each Spark assembly, flattening `<c>`, `<see cref>` and
`<para>` to plain text. A description written by hand in the generator is kept. **A new property in
an `App_Data` file needs a `<summary>`**, or it shows no help on hover. Output is deterministic, because CI compares it byte-for-byte with the latest
release. Guard tests check that generation is deterministic, that every `App_Data` file under `apps/`
and `libs/` validates, that strictness is falsifiable (`isVisible`, a bad flag name and an
un-prefixed comment are rejected), and that each schema matches what its loader reads.

A local build compiles in the revision of the nearest `schemas/v*` tag
(`git describe --tags --match "schemas/v*"`), or 0 when there is none; CI passes it explicitly
(`SparkSchemaRevision`).

## See also

- [Authorization](guide-authorization.md) — what `security.json` means
- [Triggers and refresh](guide-triggers-refresh.md) — `showedOn` at run time
