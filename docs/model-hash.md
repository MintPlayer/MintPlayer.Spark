# The model hash

Spark records a fingerprint of your model in `App_Data/modelHashes.json`, and a deployed
application **refuses to start** if it no longer matches. This page covers what the hash does and
does not cover, how to regenerate it, and what to do when it fires.

## Why

A model that no longer describes its entity classes does not fail loudly on its own. It shows up as
missing columns, attributes with the wrong type, and values silently dropped on save — symptoms that
read as data loss rather than a stale build. Checking at startup turns that into one clear failure
before the application serves a request.

## What is covered

Two independent things are hashed, because neither sees what the other does.

**The shape of your entity classes** — what the model *should* contain:

- per entity: full type name, projection/query type, index name, `[Breadcrumb]` template
- per property: name, data type, array-ness, read/write, nullability, `AsDetail` type,
  `[Reference]` target and query, `[LookupReference]` type, `[Sortable]`

**The structural content of `App_Data/Model`** — what is actually on disk:

- per file: entity name, CLR type, alias, query type, index name
- per attribute: name, data type, required, read-only, array-ness, reference target, detail type,
  lookup type, sortability, projection membership, and **validation rules**

**`actions.json` and `programUnits.json`** (`configFiles`), structurally, for the same reason: an action
absent from `actions.json` cannot run, and its `selectionRule` bounds how many rows it may be handed.

The file and config hashes are of the **composed** files: the layers referenced libraries ship (see
[Library layers](guide-library-layers.md)) with your own files on top. A type only a library ships,
such as Authorization's `SparkUser`, has a hash like any file of yours.

The file side is what makes the check tamper-evident. The shape hashes only describe what your
classes require, so on their own they would not notice a `.json` planted in the model directory — and
the loader reads whatever is in that directory.

## What is deliberately not covered

Model JSON is hand-editable by design, and synchronization preserves those edits. None of the
following affects the hash:

`label` and its translations · `description` · `breadcrumb` authored in JSON · `renderer` and
`rendererOptions` · `group` and `tabs` · `editMode` · `referenceDisplayType` · `showedOn` · `order` ·
`columnSpan` · generated `id` values · attribute ordering · `$schema` and `_` comment properties ·
indentation and line endings

`isVisible` no longer exists (#264). It was hashed while it doubled as a write gate; the one
model-level write gate is now `isReadOnly`, which is hashed. A model file still saying
`"isVisible": false` refuses startup.

Two CLR changes are also invisible, on purpose, because they generate a byte-identical model:
`int` → `long` (both are `number`), and `List<string>` → `string[]`.

> Validation rules are the one judgement call. They sit closer to presentation than to type shape,
> but silently dropping a rule from a deployed model weakens what the server accepts — that is an
> attack, not a restyling. So rules are hashed, and hand-adding one requires a synchronize run.

## Version 2: which layer moved

`modelHashes.json` is at `"version": 2`. Beside the hashes it records two things, only when a library
states a layer:

- **`libraries`** — alias → assembly (`"authorization": "MintPlayer.Spark.Authorization"`), so a
  message can name the package behind an alias.
- **`layers`** — for each composed entry a library states (`Model/<file>`, `actions.json`,
  `programUnits.json`), the structural hash of what each layer alone states, keyed by alias, with
  `app` for your own file. An entry only you state has no `layers` row: its hash already is its one
  layer's.

The layers are rolled up into `modelHash` (a `layers:` line in its canonical text), so the committed
file cannot go stale against them. A library update that moves a structure turns the gate red even
where your delta happens to restate the same value; the re-synchronization that follows is the
review. The assembly **version** is deliberately not recorded: every release bumps it, so a version
bump that changed nothing covered changes nothing here.

A drift message names the layer:

```
file SparkUser.json: expected 1a2b3c4d5e6f…  actual 6f5e4d3c2b1a… — layer library 'authorization' (MintPlayer.Spark.Authorization) changed
file Passkeys.json: shipped by library 'authorization' (MintPlayer.Spark.Authorization) but not in modelHashes.json (added since the model was generated)
file SparkUser.json: … — layer library 'authorization' (MintPlayer.Spark.Authorization) changed, the application's file changed
layers of Model/X.json: the composed structure is unchanged, but the layers stating it moved: …
library authorization (MintPlayer.Spark.Authorization): newly states a layer of the model, actions or program units
library authorization (MintPlayer.Spark.Authorization): no longer states a layer (removed, or its layers were dropped)
```

A layer is `changed`, `added` or `removed`. A version 1 file has no layers to compare; when nothing
else differs, the message says so and asks for one synchronize to record them.

## Regenerating

```bash
dotnet run --spark-synchronize-model
```

Rewrites `App_Data/Model/*.json` and `App_Data/modelHashes.json`. It needs **no database** — it
reflects over property types and never opens a session — so it runs anywhere, including CI.

## Verifying in CI

```bash
dotnet build <project> -c Debug
dotnet run --project <project> --no-build -c Debug -- --spark-verify-model
```

Debug, not Release: attribute descriptions seeded from `///` summaries live in `[Conditional("DEBUG")]`
metadata (see [Attribute descriptions](guide-attribute-descriptions.md)), so only a Debug build can
tell whether the English text on disk is stale. The structural hash is identical in both configurations;
this is what the repository's own workflow runs.

Writes nothing; exits `3` if the model has drifted and names the entities and files that moved. This
is the merge-queue gate: it catches a change that touched the entity classes without regenerating the
model, at review time rather than at deployment.

`--no-build` matters — without it `dotnet run` rebuilds, and the build consumes `App_Data/Model/*.json`
as `AdditionalFiles`.

| exit | meaning |
|---|---|
| 0 | in sync |
| 1 | synchronization threw (type mismatch, breadcrumb validation) |
| 2 | misconfiguration — no registered `SparkContext`, or it has no parameterless constructor |
| 3 | drift |

## Merge conflicts

Two pull requests that both change the model will both change `modelHashes.json`. Per-entity and
per-file hashes keep unrelated changes on separate lines, so most merges resolve themselves; the
roll-up lines conflict more often.

The resolution is always the same, and it is never "pick a side":

```bash
# empty the file, regenerate it, stage it
> App_Data/modelHashes.json
dotnet run --spark-synchronize-model
git add App_Data/modelHashes.json
```

Synchronization never *reads* the hash file — it recomputes and overwrites — so emptying it cannot
corrupt anything.

## When the check fires

The error names the entities and files that moved. One entity drifting usually means a code change
that skipped synchronization. *Every* entity drifting usually means `App_Data` was published from a
different build than the binaries — redeploy both from one commit.

In **Development** the check warns instead of throwing: drift there is normal while you are editing,
and failing hard would block the very command that fixes it.

### Emergency override

```bash
SPARK_MODEL_HASH_OVERRIDE=<the "actual" hash from the error message>
```

Starts the application despite the mismatch, warning on every startup.

It takes the actual hash rather than a boolean on purpose. The value names one build's model, so the
next model change invalidates it and the application throws again — it cannot be baked into a Helm
chart or a base image and quietly become permanent. A wrong or stale value fails closed. There is
deliberately no `Off` switch.

## A wrinkle worth knowing

Synchronization **preserves** attributes that no longer have a CLR property, rather than deleting
them (see `docs/issue_253_PRD.md`). So after you remove a property:

1. the hash changes and the check fires,
2. you run synchronize,
3. synchronize keeps the orphaned attribute and prints
   `Kept attribute 'X' on 'Y': no matching CLR property.`,
4. the hash file is rewritten and the application starts — with the orphan still there.

The recovery is "re-run synchronize **and read what it printed**". If the orphan was `isRequired`,
every save of that type will fail validation until you remove it from the JSON.
