# Reference ids bound to their declared collection, on read and on save (#251, #609)

**Packages:** NuGet `MintPlayer.Spark` → a minor. The versioning engine computes the number from the tags; the major
stays 11 (.NET 11). It ships once the merged pull request is backported to `release/dotnet-11.x`. No npm package changes.

A reference value is a bare id taken from the caller, and RavenDB's typed load does not check a document's collection. A
`Person`-typed `Driver` holding `accounts/1` was therefore read as a person: the breadcrumb showed the account's fields
under the person's label, and row security judged it as a person. #609 binds every reference id to the collection its
`referenceType` maps to, on both halves.

Decisions and evidence: [breadcrumbs-plan.md](breadcrumbs-plan.md), "Follow-up — #251". These notes were written after
the merge and carried into #606's pull request.

---

## Changed behaviour

1. **A save refuses a reference id that names no document in the target collection.** A foreign document and a missing
   one are refused alike, with one message that names neither the id nor what was found ("`Driver` refers to a record
   that does not exist.", in English, French and Dutch), `RuleType` = `referenceTarget`, HTTP 400. Nothing is written.
   - Only ids the save **newly writes** are judged. An id the stored row already holds passes, so a row with an old
     dangling reference stays editable.
   - Reference arrays and references inside AsDetail rows are checked per id; the error names the top-level attribute.
   - Attributes the caller may not write, and read-only ones, are not judged.
   - Passes: any document in the target collection (whatever its id prefix or stale `@Raven-Clr-Type`), a genuine
     subtype stored in its own collection behind a base-typed reference, and a target stored earlier in the same request
     but not yet committed.
   - Not judged: a `[ValueObject]` target and a `referenceType` with no C# class, which have no documents.
   - Sync actions applied by the owner module go through the same check, so the owner's database must hold the targets
     ([guide-cross-module-sync.md](guide-cross-module-sync.md#reference-ids-are-checked-by-the-owner)).
2. **The breadcrumb resolver drops a target of another collection.** A loaded reference whose `@collection` is not the
   declared type's renders as a missing target (its raw id), and row security is not asked about it.

## Fixed

3. **A reference breadcrumb no longer shows `JObject`.** An untyped load of a document with a stale `@Raven-Clr-Type`
   returned a `JObject`; it is now loaded as the declared type (commit `b632d521`, test added in #609).

## Breaking changes

4. **An embedded reference target must be a `[ValueObject]`.** The guide used to allow any embedded type with an `Id`.
   Such a target has no documents, so the save check refuses its ids unless the type is a registered `[ValueObject]`
   (owner decision, 2026-11-09). No app in this repository was affected: every `referenceType` in `apps/*/App_Data` is a
   document type or a `[ValueObject]`. See
   [guide-reference-attributes.md](guide-reference-attributes.md#the-target-does-not-have-to-be-a-document-type).
5. **Code that saved dangling or foreign reference ids now gets a 400.** Store the target first (in the same request
   works) or clear the reference.
