// #536 spike S-ID1 (+ the RavenDB half of S-ID5): Id<T> on RavenDB 7.2.
// Throwaway. Runs against the local dev server on localhost:8080, database spike_sid1 (recreated).
using System.Net.Http.Json;
using System.Reflection;
using Newtonsoft.Json;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Subscriptions;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;

const string Url = "http://localhost:8080";
const string Db = "spike_sid1";

var results = new List<(string Check, bool Ok, string Detail)>();
void Report(string check, bool ok, string detail) { results.Add((check, ok, detail)); Console.WriteLine($"[{(ok ? "OK  " : "FAIL")}] {check}: {detail}"); }

var store = new DocumentStore { Urls = [Url], Database = Db };
store.Conventions.Serialization = new Raven.Client.Json.Serialization.NewtonsoftJson.NewtonsoftJsonSerializationConventions();
// D23 rule 6: the hidden pass-through is the identity; the public Id<T> is an ordinary body field.
store.Conventions.FindIdentityProperty = m => m.Name == "__DocumentId" || (m.DeclaringType == typeof(LegacyCar) && m.Name == "Id");
store.Conventions.FindCollectionName = t => t == typeof(LegacyCar) ? "Cars" : Raven.Client.Documents.Conventions.DocumentConventions.DefaultGetCollectionName(t);
// D7: lowercase prefix for HiLo and anything that reads the transform.
store.Conventions.TransformTypeCollectionNameToDocumentIdPrefix = n => n.ToLowerInvariant();
// Query option (a): Id<T> renders as its string in RQL.
store.Conventions.RegisterQueryValueConverter<Id<Person>>((string field, Id<Person> v, bool range, out string obj) => { obj = v.Value; return true; });
store.Conventions.RegisterQueryValueConverter<Id<Company>>((string field, Id<Company> v, bool range, out string obj) => { obj = v.Value; return true; });
// S-ID5: ApplySparkConventions' generated and natural ids, lowered (today: $"{GetCollectionName(type)}/{guid}" and entity.GetId()).
store.Conventions.RegisterAsyncIdConvention<Tag>((_, t) => Task.FromResult($"Tags/{t.Slug}".ToLowerInvariant()));
store.Conventions.AsyncDocumentIdGenerator = (_, e) => Task.FromResult($"{store.Conventions.GetCollectionName(e.GetType())}/{Guid.NewGuid()}".ToLowerInvariant());
var storedIds = new List<string>();
store.OnBeforeStore += (_, e) => storedIds.Add(e.DocumentId);
store.Initialize();

try { await store.Maintenance.Server.SendAsync(new DeleteDatabasesOperation(Db, hardDelete: true, timeToWaitForConfirmation: TimeSpan.FromSeconds(30))); } catch { }
await store.Maintenance.Server.SendAsync(new CreateDatabaseOperation(new DatabaseRecord(Db)));

using var http = new HttpClient { BaseAddress = new Uri(Url) };
async Task<string?> Raw(string id)
{
    var r = await http.GetAsync($"/databases/{Db}/docs?id={Uri.EscapeDataString(id)}");
    return r.IsSuccessStatusCode ? await r.Content.ReadAsStringAsync() : null;
}
async Task PutRaw(string id, string json)
{
    var r = await http.PutAsync($"/databases/{Db}/docs?id={Uri.EscapeDataString(id)}", new StringContent(json));
    r.EnsureSuccessStatusCode();
}

var ann = new Person { Id = Id<Person>.FromTrusted($"people/{Guid.NewGuid():N}"), Name = "Ann" };
var acme = new Company { Id = Id<Company>.FromTrusted($"companies/{Guid.NewGuid():N}"), Name = "Acme" };
ann.CompanyId = acme.Id;
var bob = new Person { Id = Id<Person>.FromTrusted($"people/{Guid.NewGuid():N}"), Name = "Bob" };

// C1 store + load
using (var s = store.OpenAsyncSession())
{
    await s.StoreAsync(ann); await s.StoreAsync(bob); await s.StoreAsync(acme);
    await s.SaveChangesAsync();
}
var annRaw = await Raw(ann.Id.Value) ?? "";
Report("C1 stored under the canonical id", annRaw.Length > 0, ann.Id.Value);
Report("C1 body has Id as a plain string, no __DocumentId", annRaw.Contains($"\"Id\":\"{ann.Id.Value}\"") && !annRaw.Contains("__DocumentId"), Trim(annRaw));
Report("C1 reference is a plain string", annRaw.Contains($"\"CompanyId\":\"{acme.Id.Value}\""), "");
using (var s = store.OpenAsyncSession())
{
    var p = await s.LoadAsync<Person>(ann.Id.Value);
    Report("C1 load sets Id from the identity", p?.Id == ann.Id && p.CompanyId == acme.Id, $"{p?.Id} / {p?.CompanyId}");
    var up = await s.LoadAsync<Person>(ann.Id.Value.ToUpperInvariant());
    Report("C1 upper-case load finds it, same instance", ReferenceEquals(p, up), $"{up?.Id}");
}

// C2 a non-public identity member
var hid = new Hidden { Name = "h" };
hid.SetId($"hiddens/{Guid.NewGuid():N}");
try
{
    using (var s = store.OpenAsyncSession()) { await s.StoreAsync(hid); await s.SaveChangesAsync(); }
    using (var s = store.OpenAsyncSession())
    {
        var h = await s.LoadAsync<Hidden>(hid.Id.Value);
        var raw = await Raw(hid.Id.Value);
        Report("C2 private __DocumentId used as identity", h is not null && h.Id == hid.Id && raw is not null, $"{h?.Id}; raw={(raw is null ? "missing" : "present")}");
    }
}
catch (Exception ex) { Report("C2 private __DocumentId used as identity", false, ex.GetType().Name + ": " + ex.Message); }

// C3-C5 queries through the converter
using (var s = store.OpenAsyncSession())
{
    var q = s.Query<Person>().Where(x => x.Id == ann.Id);
    var list = await q.ToListAsync();
    Report("C3 Where(x => x.Id == v)", list.Count == 1 && list[0].Name == "Ann", q.ToString()!);

    var ids = new List<Id<Person>> { ann.Id, bob.Id };
    var arr = ids.ToArray(); var a0 = arr[0]; var a1 = arr[1];
    async Task TryQ(string name, IRavenQueryable<Person> q, Func<List<Person>, bool> ok)
    {
        try { var l = await q.ToListAsync(); Report(name, ok(l), q.ToString()!); }
        catch (Exception ex) { Report(name, false, ex.GetType().Name + ": " + (ex.InnerException?.Message ?? ex.Message)); }
    }
    await TryQ("C4 list.Contains(x.Id)", s.Query<Person>().Where(x => ids.Contains(x.Id)), l => l.Count == 2);
    await TryQ("C4 array.Contains(x.Id)", s.Query<Person>().Where(x => arr.Contains(x.Id)), l => l.Count == 2);
    await TryQ("C4 x.Id.In(list)", s.Query<Person>().Where(x => x.Id.In(ids)), l => l.Count == 2);
    await TryQ("C4 x.Id.In(array)", s.Query<Person>().Where(x => x.Id.In(arr)), l => l.Count == 2);
    await TryQ("C4 x.Id == a || x.Id == b", s.Query<Person>().Where(x => x.Id == a0 || x.Id == a1), l => l.Count == 2);
    await TryQ("C4 reference Where(x => x.CompanyId == v)", s.Query<Person>().Where(x => x.CompanyId == acme.Id), l => l.Count == 1);
    var cids = new List<Id<Company>?> { acme.Id };
    await TryQ("C4 reference list.Contains(x.CompanyId)", s.Query<Person>().Where(x => cids.Contains(x.CompanyId)), l => l.Count == 1);
    var names = new List<string> { "Ann", "Bob" };
    await TryQ("C4 control: list<string>.Contains(x.Name)", s.Query<Person>().Where(x => names.Contains(x.Name)), l => l.Count == 2);
    await TryQ("C4 (b) rewritten list.Contains(x.Id)", s.Query<Person>().Where(ContainsToIn.Rewrite<Person>(x => ids.Contains(x.Id))), l => l.Count == 2);
    await TryQ("C4 (b) rewritten array.Contains(x.Id)", s.Query<Person>().Where(ContainsToIn.Rewrite<Person>(x => arr.Contains(x.Id))), l => l.Count == 2);
    await TryQ("C4 (b) rewritten list.Contains(x.CompanyId)", s.Query<Person>().Where(ContainsToIn.Rewrite<Person>(x => cids.Contains(x.CompanyId))), l => l.Count == 1);
    await TryQ("C5 OrderBy(x => x.Id)", s.Query<Person>().OrderBy(x => x.Id), l => l.Count == 2 && string.CompareOrdinal(l[0].Id.Value, l[1].Id.Value) < 0);
    await TryQ("C5 OrderByDescending(x => x.Id)", s.Query<Person>().OrderByDescending(x => x.Id), l => l.Count == 2 && string.CompareOrdinal(l[0].Id.Value, l[1].Id.Value) > 0);

    try
    {
        var q5 = s.Query<Person>().Where(x => x.Name == "Ann").Select(x => new { x.Id, x.Name });
        var list5 = await q5.ToListAsync();
        Report("C5 projection Select(new { x.Id })", list5.Count == 1 && list5[0].Id == ann.Id, $"{q5}; got {list5.FirstOrDefault()?.Id}");
    }
    catch (Exception ex) { Report("C5 projection Select(new { x.Id })", false, ex.GetType().Name + ": " + ex.Message); }
}

// C6 a document written outside the framework, without the Id body field
var outsider = $"people/{Guid.NewGuid():N}";
await PutRaw(outsider, """{"Name":"Outsider","@metadata":{"@collection":"People","Raven-Clr-Type":"Person, sid1"}}""");
using (var s = store.OpenAsyncSession())
{
    var o = await s.LoadAsync<Person>(outsider);
    Report("C6 load of a doc without the Id field sets Id", o?.Id.Value == outsider, $"{o?.Id}");
    var hits = await s.Query<Person>().Customize(c => c.WaitForNonStaleResults()).Where(x => x.Id == Id<Person>.FromTrusted(outsider)).ToListAsync();
    Report("C6 query by Id finds a doc without the Id field", hits.Count == 1, $"{hits.Count} hit(s)");
}

// C7/C8 body Id disagrees with the document id
var liar = $"people/{Guid.NewGuid():N}";
await PutRaw(liar, """{"Id":"people/someoneelse","Name":"Liar","@metadata":{"@collection":"People","Raven-Clr-Type":"Person, sid1"}}""");
using (var s = store.OpenAsyncSession())
{
    var l = await s.LoadAsync<Person>(liar);
    Report("C7 identity wins over a disagreeing body Id on load", l?.Id.Value == liar, $"{l?.Id}");
    var byBody = await s.Query<Person>().Customize(c => c.WaitForNonStaleResults()).Where(x => x.Id == Id<Person>.FromTrusted("people/someoneelse")).ToListAsync();
    var byDoc = await s.Query<Person>().Customize(c => c.WaitForNonStaleResults()).Where(x => x.Id == Id<Person>.FromTrusted(liar)).ToListAsync();
    Report("C8 RQL 'Id' field: matches the body (not id())", byBody.Count == 1 && byDoc.Count == 0, $"byBody={byBody.Count}, byDocId={byDoc.Count}");
}

// C9 mixed-case legacy id (S-ID4/S-ID5 preview)
var legacy = $"People/Legacy{Guid.NewGuid():N}".Substring(0, 30);
await PutRaw(legacy, $$$"""{"Id":"{{{legacy}}}","Name":"Legacy","@metadata":{"@collection":"People","Raven-Clr-Type":"Person, sid1"}}""");
using (var s = store.OpenAsyncSession())
{
    var a = await s.LoadAsync<Person>(legacy);
    var b = await s.LoadAsync<Person>(legacy.ToLowerInvariant());
    Report("C9 legacy mixed-case doc loads lowercase, one instance", a is not null && ReferenceEquals(a, b) && a.Id.Value == legacy.ToLowerInvariant(), $"{a?.Id}");
    var prefix = await s.Advanced.LoadStartingWithAsync<Person>("people/legacy");
    Report("C9 LoadStartingWith lowercase prefix finds mixed-case id", prefix.Any(p => p.Name == "Legacy"), $"{prefix.Count()} hit(s)");
    a!.Name = "Legacy2";
    await s.SaveChangesAsync();
}
var legacyRaw = await Raw(legacy);
var legacyLower = await Raw(legacy.ToLowerInvariant());
Report("C9 save after lowered identity: still one document", legacyRaw is not null && legacyLower is not null && legacyRaw.Contains("Legacy2"), Trim(legacyRaw ?? "missing"));
var stats = await store.Maintenance.SendAsync(new GetCollectionStatisticsOperation());
Report("C9 no duplicate created", stats.Collections.TryGetValue("People", out var n) && n == 5, $"People={n}");
var idCase = await http.GetFromJsonAsync<System.Text.Json.JsonElement>($"/databases/{Db}/docs?id={Uri.EscapeDataString(legacy)}&metadataOnly=true");
Report("C9 stored id casing after save", true, Trim(idCase.ToString()));

Report("G1 OnBeforeStore DocumentId for the legacy save (debug guard input)", true, string.Join(", ", storedIds.Where(i => i.Contains("egacy", StringComparison.OrdinalIgnoreCase))));
storedIds.Clear();
var gen = new Company { Name = "Generated" };
var tag = new Tag { Slug = "DotNet" };
using (var s = store.OpenAsyncSession()) { await s.StoreAsync(gen); await s.StoreAsync(tag); await s.SaveChangesAsync(); }
Report("G2 generated id lowercase", gen.Id.Value.StartsWith("companies/") && storedIds.Contains(gen.Id.Value), gen.Id.Value);
var tagRaw = await http.GetStringAsync($"/databases/{Db}/docs?id=tags/dotnet&metadataOnly=true");
Report("G3 natural id lowercase", storedIds.Contains("tags/dotnet") && tagRaw.Contains("\"@id\":\"tags/dotnet\""), string.Join(", ", storedIds));

// C10 streaming, C11 lazy
using (var s = store.OpenAsyncSession())
{
    var streamed = new List<Person>();
    await using (var it = await s.Advanced.StreamAsync(s.Query<Person>()))
        while (await it.MoveNextAsync()) streamed.Add(it.Current.Document);
    Report("C10 StreamAsync sets Id from the identity", streamed.Count == 5 && streamed.All(p => !p.Id.IsEmpty && p.Id.Value == p.Id.Value.ToLowerInvariant()), string.Join(", ", streamed.Select(p => p.Id.Value.Split('/')[1][..Math.Min(8, p.Id.Value.Split('/')[1].Length)])));
    var lazy = s.Advanced.Lazily.LoadAsync<Person>(ann.Id.Value);
    var lp = await lazy.Value;
    Report("C11 lazy load", lp?.Id == ann.Id, $"{lp?.Id}");
}

// C12 subscription
var subName = await store.Subscriptions.CreateAsync<Person>();
var worker = store.Subscriptions.GetSubscriptionWorker<Person>(new SubscriptionWorkerOptions(subName) { CloseWhenNoDocsLeft = true });
var subIds = new List<string>();
try { await worker.Run(batch => { foreach (var i in batch.Items) subIds.Add(i.Result.Id.Value); }); }
catch (Raven.Client.Exceptions.Documents.Subscriptions.SubscriptionClosedException) { }
Report("C12 subscription worker gets Id from the identity", subIds.Count == 5 && subIds.All(i => i.StartsWith("people/")), string.Join(", ", subIds.Select(i => i.Length > 16 ? i[..16] : i)));

// C13 HiLo, lowered
var hilo = new Raven.Client.Documents.Identity.AsyncMultiDatabaseHiLoIdGenerator(store); var hiloId = await hilo.GenerateDocumentIdAsync(Db, new Person()); var hiloId2 = await hilo.GenerateDocumentIdAsync(Db, new Person()); Console.WriteLine($"HiLo second: {hiloId2}");
var lowered = hiloId.ToLowerInvariant();
var h1 = new Person { Id = Id<Person>.FromTrusted(lowered), Name = "HiLo" };
using (var s = store.OpenAsyncSession()) { await s.StoreAsync(h1); await s.SaveChangesAsync(); }
using (var s = store.OpenAsyncSession())
{
    var hl = await s.LoadAsync<Person>(hiloId);
    Report("C13 HiLo id lowered, found by the original casing", hl?.Id.Value == lowered, $"generated {hiloId} -> {lowered}");
}
Report("C14 prefix transform lowered", store.Conventions.TransformTypeCollectionNameToDocumentIdPrefix(store.Conventions.GetCollectionName(typeof(Person))) == "people", store.Conventions.GetCollectionName(typeof(Person)));

// R: option (b) extended. x.Id is rewritten to the identity member, so the client emits id().
using (var s = store.OpenAsyncSession())
{
    async Task TryR<TE>(string name, IRavenQueryable<TE> q, Func<List<TE>, bool> ok)
    {
        try { var l = await q.Customize(c => c.WaitForNonStaleResults()).ToListAsync(); Report(name, ok(l), q.ToString()!); }
        catch (Exception ex) { Report(name, false, ex.GetType().Name + ": " + (ex.InnerException?.Message ?? ex.Message)); }
    }
    var annId = ann.Id;
    await TryR("R1 x.Id == v -> id()", s.Query<Person>().Where(IdToIdentity.Rewrite<Person>(x => x.Id == annId)), l => l.Count == 1 && l[0].Name == "Ann");
    var outsiderId = Id<Person>.FromTrusted(outsider);
    await TryR("R2 doc without the body Id is found", s.Query<Person>().Where(IdToIdentity.Rewrite<Person>(x => x.Id == outsiderId)), l => l.Count == 1);
    var liarId = Id<Person>.FromTrusted(liar); var bodyId = Id<Person>.FromTrusted("people/someoneelse");
    await TryR("R3 disagreeing body: doc id matches", s.Query<Person>().Where(IdToIdentity.Rewrite<Person>(x => x.Id == liarId)), l => l.Count == 1);
    await TryR("R3 disagreeing body: body value does not", s.Query<Person>().Where(IdToIdentity.Rewrite<Person>(x => x.Id == bodyId)), l => l.Count == 0);
    var legacyId = Id<Person>.FromTrusted(legacy);
    await TryR("R4 lowercase value matches a mixed-case stored id", s.Query<Person>().Where(IdToIdentity.Rewrite<Person>(x => x.Id == legacyId)), l => l.Count == 1);
    var rids = new List<Id<Person>> { ann.Id, outsiderId, legacyId };
    await TryR("R5 list.Contains(x.Id) -> id() in", s.Query<Person>().Where(IdToIdentity.Rewrite<Person>(x => rids.Contains(x.Id))), l => l.Count == 3);
    await TryR("R5 x.Id != v", s.Query<Person>().Where(IdToIdentity.Rewrite<Person>(x => x.Id != annId)), l => l.Count == 5 && l.All(p => p.Id != annId));
    await TryR("R6 OrderBy(x => x.Id) -> id()", s.Query<Person>().OrderBy(IdToIdentity.Key<Person>(x => x.Id)), l => l.Count == 6 && l.Select(p => p.Id.Value).SequenceEqual(l.Select(p => p.Id.Value).OrderBy(v => v, StringComparer.Ordinal)));
    var hidId = hid.Id;
    await TryR("R7 private identity member in a query", s.Query<Hidden>().Where(IdToIdentity.Rewrite<Hidden>(x => x.Id == hidId)), l => l.Count == 1);
}

// P: Id<T> not stored in the body at all ([JsonIgnore]); documents stay byte-identical to today's string-Id documents.
var legacyShape = new LegacyCar { Id = $"cars/{Guid.NewGuid():N}", Plate = "1-ABC-123" };
using (var s = store.OpenAsyncSession()) { await s.StoreAsync(legacyShape); await s.SaveChangesAsync(); }
var beforeRaw = await Raw(legacyShape.Id);
using (var s = store.OpenAsyncSession())
{
    var c = await s.LoadAsync<Car>(legacyShape.Id);
    Report("P1 today's string-Id doc loads as Id<T>", c?.Id.Value == legacyShape.Id, $"{c?.Id}");
    c!.Plate = "1-ABC-124"; await s.SaveChangesAsync();
    c.Plate = "1-ABC-123"; await s.SaveChangesAsync();
}
var afterRaw = await Raw(legacyShape.Id);
static string Body(string? raw) { var j = Newtonsoft.Json.Linq.JObject.Parse(raw!)["Results"]![0]!; ((Newtonsoft.Json.Linq.JObject)j["@metadata"]!).Remove("@change-vector"); ((Newtonsoft.Json.Linq.JObject)j["@metadata"]!).Remove("@last-modified"); ((Newtonsoft.Json.Linq.JObject)j["@metadata"]!).Remove("Raven-Clr-Type"); return j.ToString(Formatting.None); }
Report("P2 re-save is byte-identical (minus change vector, timestamp, clr type)", Body(beforeRaw) == Body(afterRaw), Body(afterRaw));
using (var s = store.OpenAsyncSession())
{
    var cid = Id<Car>.FromTrusted(legacyShape.Id);
    try
    {
        var q = s.Query<Car>().Customize(c => c.WaitForNonStaleResults()).Where(IdToIdentity.Rewrite<Car>(x => x.Id == cid));
        var l = await q.ToListAsync();
        Report("P3 query on an ignored Id via id()", l.Count == 1, q.ToString()!);
    }
    catch (Exception ex) { Report("P3 query on an ignored Id via id()", false, ex.Message); }
    try
    {
        var q = s.Query<Car>().Customize(c => c.WaitForNonStaleResults()).Where(x => x.Plate == "1-ABC-123").Select(x => new { DocId = x.__DocumentId, x.Plate });
        var l = await q.ToListAsync();
        Report("P4 projection of the identity member", l.Count == 1 && l[0].DocId == legacyShape.Id, $"{q}; got {l.FirstOrDefault()?.DocId}");
    }
    catch (Exception ex) { Report("P4 projection of the identity member", false, ex.Message); }
}

Console.WriteLine();
Console.WriteLine($"SUMMARY {results.Count(r => r.Ok)}/{results.Count} OK");
foreach (var r in results.Where(r => !r.Ok)) Console.WriteLine($"  FAIL {r.Check}");
return 0;

static string Trim(string s) => s.Length > 220 ? s[..220] + "…" : s;

[JsonConverter(typeof(IdConverter))]
public readonly struct Id<T> : IEquatable<Id<T>>
{
    private readonly string? value;
    private Id(string v) => value = v;
    public string Value => value ?? "";
    public bool IsEmpty => value is null;
    public static Id<T> FromTrusted(string v) => new(v.ToLowerInvariant());
    public bool Equals(Id<T> other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is Id<T> o && Equals(o);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
    public static bool operator ==(Id<T> a, Id<T> b) => a.Equals(b);
    public static bool operator !=(Id<T> a, Id<T> b) => !a.Equals(b);
    public override string ToString() => Value;
}

// Option (b): rewrite coll.Contains(x.Member) over Id<T> elements into x.Member.In(coll), which the provider supports.
public sealed class ContainsToIn : System.Linq.Expressions.ExpressionVisitor
{
    public static System.Linq.Expressions.Expression<Func<T, bool>> Rewrite<T>(System.Linq.Expressions.Expression<Func<T, bool>> e)
        => (System.Linq.Expressions.Expression<Func<T, bool>>)new ContainsToIn().Visit(e);

    private static bool IsId(Type t) => (Nullable.GetUnderlyingType(t) ?? t) is { IsGenericType: true } g && g.GetGenericTypeDefinition() == typeof(Id<>);

    protected override System.Linq.Expressions.Expression VisitMethodCall(System.Linq.Expressions.MethodCallExpression node)
    {
        System.Linq.Expressions.Expression? coll = null, item = null;
        if (node.Method.Name == "Contains" && node.Method.IsStatic && node.Arguments.Count == 2) { coll = node.Arguments[0]; item = node.Arguments[1]; }
        else if (node.Method.Name == "Contains" && !node.Method.IsStatic && node.Arguments.Count == 1) { coll = node.Object; item = node.Arguments[0]; }
        // C# 14 binds array.Contains(x) to MemoryExtensions.Contains(ReadOnlySpan<T>, T): unwrap the implicit span conversion.
        if (coll is System.Linq.Expressions.MethodCallExpression { Method.Name: "op_Implicit" } conv) coll = conv.Arguments[0];
        if (coll is null || item is null || !IsId(item.Type)) return base.VisitMethodCall(node);
        var elem = item.Type;
        var enumerable = typeof(IEnumerable<>).MakeGenericType(elem);
        var inMethod = typeof(RavenQueryableExtensions).GetMethods()
            .Single(m => m.Name == "In" && m.GetParameters()[1].ParameterType.IsGenericType && m.GetParameters()[1].ParameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .MakeGenericMethod(elem);
        return System.Linq.Expressions.Expression.Call(inMethod, Visit(item), System.Linq.Expressions.Expression.Convert(coll, enumerable));
    }
}

// Option (b) extended: the entity's own Id<T> member becomes the hidden identity member (string), values become strings.
public sealed class IdToIdentity : System.Linq.Expressions.ExpressionVisitor
{
    public static System.Linq.Expressions.Expression<Func<T, bool>> Rewrite<T>(System.Linq.Expressions.Expression<Func<T, bool>> e)
        => (System.Linq.Expressions.Expression<Func<T, bool>>)new IdToIdentity().Visit(e);

    public static System.Linq.Expressions.Expression<Func<T, string?>> Key<T>(System.Linq.Expressions.Expression<Func<T, Id<T>>> e)
        => System.Linq.Expressions.Expression.Lambda<Func<T, string?>>(Identity(e.Parameters[0]), e.Parameters);

    private static System.Linq.Expressions.MemberExpression Identity(System.Linq.Expressions.Expression p)
        => System.Linq.Expressions.Expression.Property(p, p.Type.GetProperty("__DocumentId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!);

    private static bool IsOwnId(System.Linq.Expressions.Expression e, out System.Linq.Expressions.Expression owner)
    {
        owner = null!;
        if (e is System.Linq.Expressions.UnaryExpression { NodeType: System.Linq.Expressions.ExpressionType.Convert } u) e = u.Operand;
        if (e is System.Linq.Expressions.MemberExpression { Member.Name: "Id", Expression: System.Linq.Expressions.ParameterExpression p } m
            && m.Type is { IsGenericType: true } t && t.GetGenericTypeDefinition() == typeof(Id<>) && t.GetGenericArguments()[0] == p.Type)
        { owner = p; return true; }
        return false;
    }

    private static string? Eval(System.Linq.Expressions.Expression e)
    {
        var v = System.Linq.Expressions.Expression.Lambda(e).Compile().DynamicInvoke();
        return (string?)v?.GetType().GetProperty("Value")!.GetValue(v);
    }

    protected override System.Linq.Expressions.Expression VisitBinary(System.Linq.Expressions.BinaryExpression node)
    {
        if (node.NodeType is System.Linq.Expressions.ExpressionType.Equal or System.Linq.Expressions.ExpressionType.NotEqual)
        {
            if (IsOwnId(node.Left, out var o)) return System.Linq.Expressions.Expression.MakeBinary(node.NodeType, Identity(o), System.Linq.Expressions.Expression.Constant(Eval(node.Right), typeof(string)));
            if (IsOwnId(node.Right, out o)) return System.Linq.Expressions.Expression.MakeBinary(node.NodeType, Identity(o), System.Linq.Expressions.Expression.Constant(Eval(node.Left), typeof(string)));
        }
        return base.VisitBinary(node);
    }

    protected override System.Linq.Expressions.Expression VisitMethodCall(System.Linq.Expressions.MethodCallExpression node)
    {
        System.Linq.Expressions.Expression? coll = null, item = null;
        if ((node.Method.Name is "Contains" or "In") && node.Method.IsStatic && node.Arguments.Count == 2)
        { (coll, item) = node.Method.Name == "In" ? (node.Arguments[1], node.Arguments[0]) : (node.Arguments[0], node.Arguments[1]); }
        else if (node.Method.Name == "Contains" && !node.Method.IsStatic && node.Arguments.Count == 1) { coll = node.Object; item = node.Arguments[0]; }
        if (coll is System.Linq.Expressions.MethodCallExpression { Method.Name: "op_Implicit" } conv) coll = conv.Arguments[0];
        if (coll is null || item is null || !IsOwnId(item, out var o)) return base.VisitMethodCall(node);
        var values = ((System.Collections.IEnumerable)System.Linq.Expressions.Expression.Lambda(coll).Compile().DynamicInvoke()!)
            .Cast<object>().Select(v => (string)v.GetType().GetProperty("Value")!.GetValue(v)!).ToArray();
        var inMethod = typeof(RavenQueryableExtensions).GetMethods()
            .Single(m => m.Name == "In" && m.GetParameters()[1].ParameterType.IsGenericType && m.GetParameters()[1].ParameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .MakeGenericMethod(typeof(string));
        return System.Linq.Expressions.Expression.Call(inMethod, Identity(o), System.Linq.Expressions.Expression.Constant(values, typeof(IEnumerable<string>)));
    }
}

public sealed class IdConverter : JsonConverter
{
    public override bool CanConvert(Type t) => (Nullable.GetUnderlyingType(t) ?? t) is { IsGenericType: true } g && g.GetGenericTypeDefinition() == typeof(Id<>);
    public override object? ReadJson(JsonReader reader, Type t, object? existing, JsonSerializer serializer)
    {
        var target = Nullable.GetUnderlyingType(t) ?? t;
        if (reader.TokenType == JsonToken.Null) return Nullable.GetUnderlyingType(t) is null ? Activator.CreateInstance(target) : null;
        var s = (string)reader.Value!;
        return target.GetMethod("FromTrusted", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [s]);
    }
    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        var s = (string?)value?.GetType().GetProperty("Value")!.GetValue(value);
        if (string.IsNullOrEmpty(s)) writer.WriteNull(); else writer.WriteValue(s);
    }
}

public class Person
{
    public Id<Person> Id { get; set; }
    public string Name { get; set; } = "";
    public Id<Company>? CompanyId { get; set; }
    // What the generator would emit (D23 rule 6).
    [JsonIgnore]
    public string? __DocumentId { get => Id.IsEmpty ? null : Id.Value; set => Id = value is null ? default : Id<Person>.FromTrusted(value); }
}

public class Company
{
    public Id<Company> Id { get; set; }
    public string Name { get; set; } = "";
    [JsonIgnore]
    public string? __DocumentId { get => Id.IsEmpty ? null : Id.Value; set => Id = value is null ? default : Id<Company>.FromTrusted(value); }
}

// Today's shape: string Id is the identity, stripped from the body. Same collection as Car.
public class LegacyCar
{
    public string? Id { get; set; }
    public string Plate { get; set; } = "";
}

public class Car
{
    [JsonIgnore]
    public Id<Car> Id { get; set; }
    public string Plate { get; set; } = "";
    [JsonIgnore]
    public string? __DocumentId { get => Id.IsEmpty ? null : Id.Value; set => Id = value is null ? default : Id<Car>.FromTrusted(value); }
}

public class Tag
{
    public string? Id { get; set; }
    public string Slug { get; set; } = "";
}

public class Hidden
{
    public Id<Hidden> Id { get; set; }
    public string Name { get; set; } = "";
    public void SetId(string v) => Id = Id<Hidden>.FromTrusted(v);
    [JsonIgnore]
    private string? __DocumentId { get => Id.IsEmpty ? null : Id.Value; set => Id = value is null ? default : Id<Hidden>.FromTrusted(value); }
}
