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
`.gitignore`.

### Regenerating them locally

`tools/SchemaGenerator` (`MintPlayer.Spark.SchemaGenerator`, part of the solution) writes the set to
`schemas/` after its own build, incrementally — any `dotnet build` of the solution keeps them current.
To regenerate on demand:

```
dotnet build tools/SchemaGenerator
```

The schemas are exported with `System.Text.Json`'s `JsonSchemaExporter` from the types the loaders
deserialize (`EntityTypeFile`, `SecurityConfiguration`, `ProgramUnitsConfiguration`, and mirrors of
what the culture and actions loaders read), plus a hand-built tree for `translations.json`, which no
CLR type describes. Output is deterministic, because CI compares it byte-for-byte with the latest
release. Guard tests check that generation is deterministic, that every `App_Data` file under `apps/`
and `libs/` validates, that strictness is falsifiable (`isVisible`, a bad flag name and an
un-prefixed comment are rejected), and that each schema matches what its loader reads.

A local build compiles in the revision of the nearest `schemas/v*` tag
(`git describe --tags --match "schemas/v*"`), or 0 when there is none; CI passes it explicitly
(`SparkSchemaRevision`).

## See also

- [Authorization](guide-authorization.md) — what `security.json` means
- [Triggers and refresh](guide-triggers-refresh.md) — `showedOn` at run time
