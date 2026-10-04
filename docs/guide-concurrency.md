# Guide: concurrent edits and the 409 merge

Two people open the same record, both edit, both save. Spark lets the first save through and refuses
the second with **409**; the edit page then merges the refused form onto the version that won,
asks only about the fields both people changed, and leaves the result on screen for the user to save.
Nothing in this needs configuration: every persistent object gets it, on the server and in
`@mintplayer/ng-spark`'s edit page.

This guide is about that core mechanism. [Contributions](guide-contributions.md) builds on it and
changes one rule (section 7).

## 1. What the server checks

Every persistent object a read returns carries an `etag`: RavenDB's change vector for the document as
it was read (`PersistentObject.Etag`). So does every query row (`QueryResultItem.Etag`, the
**document's** change vector, also for an index projection). The client echoes it back on save and on
delete, and the request is refused when the document has changed since.

**The etag is required (#467, D14/D16).** Over HTTP:

| Request | Without an etag | Changed since | Deleted since |
|---|---|---|---|
| `POST /spark/po/update` (`persistentObject.etag`) | 400 | 409 `changed` | 409 `deleted` |
| `POST /spark/po/delete` (`etag`) | 400 | 409 `changed` | 404 |
| `POST /spark/po/delete-many` (`items: [{ id, etag }]`) | 400 | 409 `changed`, naming the rows | 404 |

A create posts no etag. A create whose natural id another row already holds (`IHasNaturalId`) used to
become an edit of that row; it is now a 409 `exists`, because an edit must say which version it
overwrites and a create has none.

An update of an object that was deleted — or soft-deleted, or is no longer one the caller may see —
since it was loaded is a 409 `deleted`, never a resurrection (D15). One answer for all three, so it
tells nothing a 404 would not.

**`IDatabaseAccess` keeps the etag optional.** A caller that never showed the row to anyone (a job, a
sync, an import) may update or delete without one: the internal overwrite. No HTTP request reaches it,
because every endpoint refuses a missing etag first. With an etag, `IDatabaseAccess` applies the same
rules as above — `SavePersistentObjectAsync` of a loaded object whose document is gone throws the 409,
and `DeletePersistentObjectAsync(type, id, operation, etag)` /
`SparkBulkDeleteContext.Etags` check and write with it.

The check happens twice, for different reasons:

- **Early, in `DatabaseAccess`.** A posted etag that differs from the stored change vector answers
  409 before any interceptor or business rule runs.
- **At the write, which the framework owns (#482).** The early check reads in a separate session and
  cannot see a write that lands after it, so the framework writes with the expected change vector:

  ```csharp
  await session.StoreAsync(entity, expectedChangeVector, session.Advanced.GetDocumentId(entity));
  ```

  The expected change vector is the client's etag when it posted one, and otherwise (an internal
  caller) the version this save loaded. A concurrent write in between makes `SaveChangesAsync` throw
  RavenDB's `ConcurrencyException`, which `DatabaseAccess` turns into the same 409. An object posted
  with an etag whose document is gone is refused there too, rather than rebuilt from the posted
  values. A create writes without one, so an internal create under a caller-chosen id keeps its usual
  behaviour.

The second check is what makes it safe. Before it existed, a save with or without an etag could
silently overwrite a write that landed between the check and the save (`ConcurrentWriteRaceTests`).

**Deletes work the same way.** `DatabaseAccess` compares the etag after every gate, then deletes (or
stores the soft-delete replacement) with it (`session.Delete(id, expectedChangeVector)`), so an edit
landing after the check is a 409 as well. No interceptor and no Actions class can skip that: deleting is the
framework's, and an interceptor that asks for confirmation first (Fleet's `CarActions`) runs before it.

**An internal save without an etag is protected too:** it is checked against the version it loaded.
The consequence is that a replication sync or a restore that races another write gets a 409 where it
used to get last-write-wins.

The same mapping applies to `POST /spark/po/create`, single and bulk delete, a soft delete (the
replaced delete is a save), SoftDelete's restore, History's revert and Contributions' endpoints.

## 2. The 409 says nothing on purpose

```json
{ "error": "Concurrency conflict", "reason": "changed", "message": null }
```

That is the whole body. `reason` is `changed`, `deleted` (the object went since it was loaded) or
`exists` (a natural-id create collision). `message` is set only by a bulk delete, where it names the
rows that changed since the list loaded (every one of them readable, so naming them discloses
nothing). RavenDB's own message contains change vectors, which tell a caller about document versions
it may have no business knowing, so they stay in the inner exception, for logs only. The client does
not need them: it re-fetches the object (section 3), or, for a `deleted` 409, says so and keeps the
form as typed.

A refused save leaves nothing behind. The entity, and every document an interceptor stored, changed
or deleted during that save (a contribution, an audit row), is evicted from the request session, so a
later `SaveChangesAsync` in the same request cannot commit half of it (`RefusedWriteEvictionTests`).

**Your own endpoints** (an add-on package, a custom controller) answer the same way through
`SparkAddOnEndpoints`, since the exception type itself is internal:

```csharp
try
{
    await databaseAccess.SavePersistentObjectAsync(obj);
}
catch (Exception ex) when (SparkAddOnEndpoints.IsConcurrencyConflict(ex))
{
    return SparkAddOnEndpoints.ConcurrencyConflict(clientAccessor, ex);   // the same 409 envelope
}
```

## 3. What the edit page does with a 409

For a 409 `deleted` the edit page keeps the form as it is, shows "Somebody else deleted this record
while you were editing it. Your changes have not been saved." (`common.deletedByAnotherUser`), and
stops: there is nothing to merge against, and it offers no "recreate". The list's bulk delete shows a
409's `message`, which names the rows to reload.

For any other 409 the edit page (`spark-po-edit`, `@mintplayer/ng-spark/po-edit`) keeps the form as
it is, shows "Somebody else changed this record while you were editing it", and then:

1. **Re-fetches the object** with a normal read. Read rights and row security apply, so the merge
   never sees more than the user may read. If the read fails (the row is gone, or no longer
   readable), the message stays and nothing else happens.
2. **Compares three versions**, all in the form's own shape:
   - **base**: the object as the page loaded it,
   - **mine**: the form now,
   - **theirs**: the object just re-fetched.
3. **Merges per attribute**:

   | Changed by | Result |
   |---|---|
   | me only | mine |
   | them only | theirs |
   | both, to the same value | that value, no conflict |
   | both, to different values | a **conflict** |

   A read-only attribute always takes theirs, silently: the user cannot have edited it.

4. **Merges AsDetail collections per row**, matching rows by their `[ValueKey]`
   ([AsDetail attributes](guide-asdetail-attributes.md)):

   | Row | Result |
   |---|---|
   | added, removed or changed on one side only | that side |
   | added on both sides with the same key and different content | a row **conflict** (keep mine or theirs) |
   | removed on one side, edited on the other | a row **conflict** (keep or remove) |
   | changed on both sides | merged attribute by attribute, with the table above |

   A single embedded AsDetail object changed on both sides is also merged attribute by attribute.
   Row order follows **theirs**, followed by the rows only I have: rows I added (a new row has no key
   until it is saved) and rows they removed that I may keep.

If nothing conflicts, the merge is applied straight away and the page says so: "Someone else changed
*Title, Year* meanwhile; your changes were merged. Review and save." If something conflicts, the
dialog opens.

The merge is a pure function, `mergeThreeWay(base, mine, theirs, schema, choices?)`, exported from
`@mintplayer/ng-spark/po-edit` with `SparkPoConflictDialogComponent`, so a custom edit page can reuse
both. Its full matrix is in `conflict-merge.spec.ts`.

## 4. The dialog

"Conflicting changes" lists **only the true conflicts**, grouped by row. Each has a **Mine** and a
**Theirs** radio button, with the value rendered by the same `<spark-grid-cell>` renderers a query grid
uses. References show their label, not their id. A row conflict shows the row's visible attributes
cell by cell, with the differing ones marked and a missing side reading "(removed)".

- **Keep all mine** / **Take all theirs** select that side for every conflict at once. They only set
  the radio buttons, so the user can still change individual choices before applying.
- **Apply** stays disabled until every conflict has a choice.
- **Cancel** changes nothing: the form keeps its values and the conflict message stays.
- Above the list, the dialog says when they changed it ("Changed at *T*.") if the model has a
  `ModifiedAt` attribute (an `IAuditable` target with History), and "They also changed: …" names what
  the merge took from theirs without asking.

Spark ships the dialog texts in English, French and Dutch (the `common.conflict*` translation keys).

## 5. Showing who changed it: `showChangedBy`

By default the dialog says *when* the other change happened, never *who*. To name the person:

```ts
providers: [
  provideSpark({ conflictDialog: { showChangedBy: true } }),
],
```

| `showChangedBy` | What happens |
|---|---|
| `false` (default) | No History request is made. The line reads "Changed at *T*." when `ModifiedAt` is known. |
| `true` | One `POST /spark/po/revisions` request (newest revision only), made only when the user holds `History/T`. The name the app's `IHistoryUserNameResolver` gave that revision is shown, "Changed by *Alice* at *T*.", but only when that revision is exactly the version merged against. Otherwise no name is shown. |

**A raw user id is never shown in either mode.** `ModifiedBy` holds an id (History stores ids, not
names), and an id is not something to put in front of every editor. The name lookup is not awaited:
the merge and the dialog never wait for it, and a failed lookup only means no name.

A name needs History installed with a name resolver (`spark.AddHistoryUserNameResolver<T>()`).
Without one, a revision carries only the id, so `true` shows no name either.

## 6. Nothing is saved automatically

Whether the merge was silent or went through the dialog, the result goes **back into the form**.
The page is **rebased** onto their version, so:

- the next save sends **their** etag, not the stale one, and does not get a 409 again for the same
  reason,
- a second conflict, if someone saves yet again, is merged against their version,
- only what differs from their version is sent as changed.

The user reviews the merged form and presses Save. That save runs server validation and business rules
again, on the merged values. A three-way merge only decides field by field, so this second pass is what
catches a combination of two fields that is invalid even though each one merged cleanly.

## 7. Contribution rows: theirs wins

A [`[Contribution]`](guide-contributions.md) property looks like an AsDetail collection on the form,
but each row is one user's own contribution document, not a field the two editors share. So the row
rule changes:

- **Another contributor changed a row that I also changed**: theirs is kept, my edit of that row is
  dropped, and the page says so: "Another contributor saved a newer version of *ko/Kore* meanwhile; it
  is shown now and your edit to it was not kept." My save would otherwise silently supersede a version
  I never saw.
- **The same user, in a second tab**: an ordinary conflict, decided in the dialog as in section 4.
- Rows only one side changed merge as usual.

"Same user" is decided by the server, not by the client: each loaded contribution row carries
`metadata.contribution.own`, a boolean that is never the contributor's id. When they removed a row, it
is judged by who wrote the version they removed.

## 8. Limitations

- **No auto-save, and no live notice.** The conflict is found when the second user saves, not while
  they type.
- **Hard deletes are not checked.** The RavenDB client ignores the expected change vector on
  `Delete(entity)`, so a hard delete racing an edit still wins. Soft deletes are saves and are checked.
- **A save that changes nothing writes nothing**, so it gets no write-time check. With a stale etag it
  still gets the early 409.
- **Only the edit page merges.** Other callers of `/spark/po/update` (your own client, the .NET client)
  receive the 409 and decide for themselves; re-fetch and retry is the expected response.

## See also

- [Contributions](guide-contributions.md) — the latest-wins model behind section 7
- [AsDetail attributes](guide-asdetail-attributes.md) — `[ValueKey]`, which the row merge keys on
- [Authorization](guide-authorization.md) — the read rights the re-fetch is subject to
