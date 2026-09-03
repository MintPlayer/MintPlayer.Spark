# Release notes — `10.0.0-preview.71`

One fix: **a referenced document whose stored CLR type no longer resolves is no longer rendered as
the literal text `JObject`, and no longer escapes its own row-level security.**

No API change. No model change. Nothing to do on upgrade beyond taking the version.

---

## The symptom

Every reference attribute on a page — the `Account` on a repository, a lookup label in a grid cell,
the breadcrumb at the top of a detail page — rendered as the word **`JObject`** instead of the
referenced document's label.

It looked like bad data in one document, because it was intermittent per page. It was not: it
depended on when each *referenced* document had last been written.

## Why it happened

`BreadcrumbResolver` batch-loads referenced documents to render their labels, and it asked RavenDB
for them as `object`. RavenDB recovers a document's CLR type from its `@Raven-Clr-Type` metadata
when it can, and falls back to a `Newtonsoft.Json.Linq.JObject` when that metadata is absent or
names a type the process cannot resolve. That happens after a raw put, a bulk insert, an import —
or, most commonly, when an entity is **renamed or moved between assemblies**, because the metadata
records the name that was current when each document was *written*.

A `JObject` matched no entity-type definition, so the resolver fell through to a last-resort branch
that rendered the CLR type name. Hence, literally, `JObject`.

Two things followed from the same untyped load, and the second is the more serious:

- **Display.** With no definition, no breadcrumb template applied. Worse, even where a definition
  was recovered, reading a field off a `JObject` by reflection finds a property of `JObject` —
  never a document field — so every scalar came back empty.
- **Row-level security.** The referenced document was judged as `typeof(JObject)`. A row rule is
  declared over the entity type, no rule is registered for `JObject`, and "no rule" means
  unrestricted. **A document whose metadata had gone stale silently skipped the row rule its own
  type declares**, and its breadcrumb was rendered to a caller who should not have seen it.

## The fix

The reference edge already knows what it points at — `[Reference(typeof(Account))]` is in the model
— so nothing needs to be inferred from the stored document. The resolver now loads each level under
the **declared** target type, and derives both the entity-type definition and the type it hands to
row security from the model rather than from the document's metadata.

This is the same correction #281 made in `RowSecurity`, which had the identical bug for the same
reason; `BreadcrumbResolver` was the last untyped load in the framework.

Two smaller changes come with it:

- A referenced document with **no entity-type definition at all** now renders as its **id**
  (`people/1`) rather than a CLR type name. An id is a true, stable label; a type name is an
  internal detail that means nothing to the person reading the page.
- The document's own type still wins when the model knows it, so a subtype stored behind a
  base-typed reference keeps rendering its own breadcrumb.

### Cost

Loading is now batched **per distinct declared target type per level**, rather than one untyped
batch per level. A page's references span a handful of types, so this is a small constant; it is
still independent of row count and fan-out, which is the property that matters. The alternative was
a result that is wrong whenever the stored metadata is.

## If you have renamed or moved an entity

You do not need to rewrite `@Raven-Clr-Type` for this. Reads no longer depend on it: queries resolve
through `@collection` (unchanged by a namespace rename, which keys off the short type name), and
every load in the framework now names its type.

It is still worth knowing that the metadata is stale, because it is a genuine trap for
**hand-written** code: a `session.LoadAsync<object>(id)` of your own has exactly the problem
described above. Load under the entity type.

## Verified

Pinned by three tests in `BreadcrumbResolverTests`, each of which fails against the previous
behaviour with the reported symptom:

| Test | Previous result |
| --- | --- |
| `…no_longer_resolves_still_renders_its_breadcrumb` | `"JObject"` instead of `"Ada Lovelace"` |
| `…no_longer_resolves_is_still_row_gated` | `"JObject"` instead of the redacted `"—"` — the deny rule never ran |
| `…renders_its_id_never_a_clr_type_name` | `"BR_Person"` instead of `"people/1"` |

The stale document is produced the way production produced it: stored normally, then its
`@Raven-Clr-Type` rewritten to a type this process cannot resolve.

Full suite: **1906 passed, 0 failed.** The existing O(depth) request-count assertions are unchanged.

## Known adjacent case, not fixed here

A breadcrumb template that names a **`TranslatedString`** attribute (`[Breadcrumb("{Title}")]` where
`Title` is a `TranslatedString`) renders that value with `ToString()`, which yields the CLR type
name — the same shape of defect, in `BreadcrumbResolver.FormatScalar` and
`EmbeddedBreadcrumbRenderer`. No model in this repository does that today, and fixing it properly
means threading the request culture into both renderers (one of which is static and reached from
`EntityMapper`). Left alone deliberately rather than half-fixed with a culture-blind fallback.
