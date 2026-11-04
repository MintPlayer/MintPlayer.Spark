# MintPlayer.Spark.Search

Search across the entity types of a MintPlayer.Spark application (#238).

```csharp
builder.Services.AddSpark(builder.Configuration, spark => spark
    .AddSearch());
```

Opt a type in from its model JSON:

```json
"search": { "enabled": true, "label": "{Title} - {Artist}", "fuzzy": true }
```

- `label` defaults to the type's `breadcrumb`. Two visible objects with the same label get the type's label, and a
  number among the namesakes of one type, so every suggestion names exactly one object.
- Every hit is re-read through the type's list query as the caller, so query rights, row policies and redaction
  apply exactly as on the list page. A provider only proposes candidates.
- By default the app's generated `Spark_Search` RavenDB index answers. An engine package
  (`MintPlayer.Spark.Search.OpenSearch`) can take over; the RavenDB index stays the fallback while the engine is
  unreachable.

Endpoints (anonymous, rate limited per address): `GET /spark/search?q=&take=`, `GET /spark/search/suggest?q=`,
`GET /spark/search/resolve?q=`.

Design: `docs/spark_search_PRD.md`.
