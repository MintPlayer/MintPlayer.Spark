// #536 spike S-ID4: mixed-case legacy ids under the lowercase rule (O15 (a)), on a COPY of a Coverage database.
// The source database is only exported (read). Everything else runs on the copy, spike_sid4 (recreated).
// dotnet run -- [sourceDatabase]   (default Coverage, local dev server http://localhost:8080)
using System.Net.Http.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Smuggler;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;

const string Url = "http://localhost:8080";
const string Copy = "spike_sid4";
var source = args.Length > 0 ? args[0] : "Coverage";

var results = new List<(string Check, bool Ok, string Detail)>();
void Report(string check, bool ok, string detail) { results.Add((check, ok, detail)); Console.WriteLine($"[{(ok ? "OK  " : "FAIL")}] {check}: {detail}"); }

// 1. Copy: export the source (read-only), import into a fresh database.
var dump = Path.Combine(AppContext.BaseDirectory, "source.ravendbdump");
using (var src = new DocumentStore { Urls = [Url], Database = source }.Initialize())
{
    var types = DatabaseItemType.Documents | DatabaseItemType.Indexes | DatabaseItemType.Identities | DatabaseItemType.Attachments;
    var export = await src.Smuggler.ExportAsync(new DatabaseSmugglerExportOptions { OperateOnTypes = types }, dump);
    await export.WaitForCompletionAsync(TimeSpan.FromMinutes(5));
    try { await src.Maintenance.Server.SendAsync(new DeleteDatabasesOperation(Copy, hardDelete: true, timeToWaitForConfirmation: TimeSpan.FromSeconds(30))); } catch { }
    await src.Maintenance.Server.SendAsync(new CreateDatabaseOperation(new DatabaseRecord(Copy)));
    using var dst = new DocumentStore { Urls = [Url], Database = Copy }.Initialize();
    var import = await dst.Smuggler.ImportAsync(new DatabaseSmugglerImportOptions { OperateOnTypes = types }, dump);
    await import.WaitForCompletionAsync(TimeSpan.FromMinutes(5));
}

using var http = new HttpClient { BaseAddress = new Uri(Url) };
async Task<Dictionary<string, JObject>> Snapshot()
{
    var all = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
    for (var start = 0; ; start += 1024)
    {
        var page = JObject.Parse(await http.GetStringAsync($"/databases/{Copy}/docs?start={start}&pageSize=1024"));
        var arr = (JArray)page["Results"]!;
        foreach (JObject d in arr) all[(string)d["@metadata"]!["@id"]!] = d;
        if (arr.Count < 1024) break;
    }
    return all;
}
var before = await Snapshot();
var mixed = before.Keys.Count(k => k != k.ToLowerInvariant());
Report("Copy made", before.Count > 0, $"{before.Count} documents in {Copy}, {mixed} with a non-lowercase id");

var store = new DocumentStore { Urls = [Url], Database = Copy };
store.Conventions.FindIdentityProperty = m => m.Name == "__DocumentId";
store.Conventions.MaxNumberOfRequestsPerSession = 1000;
store.Conventions.Serialization = new Raven.Client.Json.Serialization.NewtonsoftJson.NewtonsoftJsonSerializationConventions
{
    CustomizeJsonSerializer = s => { s.DateParseHandling = DateParseHandling.None; s.FloatParseHandling = FloatParseHandling.Decimal; },
};
store.Conventions.FindCollectionName = t => t.GetCustomAttributes(typeof(CollAttribute), false).FirstOrDefault() is CollAttribute c ? c.Name : Raven.Client.Documents.Conventions.DocumentConventions.DefaultGetCollectionName(t);
store.Initialize();

// L1 every document loads by its lowercased id, in batches; Id reads lowercase.
var missing = 0; var notLowered = 0;
foreach (var chunk in before.Keys.Chunk(256))
{
    using var s = store.OpenAsyncSession();
    var loaded = await s.LoadAsync<AnyDoc>(chunk.Select(k => k.ToLowerInvariant()));
    foreach (var k in chunk)
    {
        var d = loaded[k.ToLowerInvariant()];
        if (d is null) missing++;
        else if (d.Id != k.ToLowerInvariant()) notLowered++;
    }
}
Report("L1 batch LoadAsync with lowercased ids finds every document", missing == 0 && notLowered == 0, $"{before.Count - missing}/{before.Count} found, {notLowered} with a non-lowercase Id");

// L2 identity map: the same document under two casings is one instance.
var sample = before.Keys.Where(k => k != k.ToLowerInvariant()).Take(50).ToList();
var same = 0;
foreach (var k in sample)
{
    using var s = store.OpenAsyncSession();
    var a = await s.LoadAsync<AnyDoc>(k);
    var b = await s.LoadAsync<AnyDoc>(k.ToLowerInvariant());
    var c = await s.LoadAsync<AnyDoc>(k.ToUpperInvariant());
    if (ReferenceEquals(a, b) && ReferenceEquals(b, c)) same++;
}
Report("L2 original, lower and upper casing load one instance", same == sample.Count, $"{same}/{sample.Count}");

// L3 LoadStartingWith with a lowercased prefix, per id prefix.
var prefixes = before.Keys.GroupBy(k => k[..(k.IndexOf('/') + 1)], StringComparer.OrdinalIgnoreCase).ToList();
var prefixOk = 0; var prefixDetail = new List<string>();
foreach (var g in prefixes)
{
    using var s = store.OpenAsyncSession();
    var got = (await s.Advanced.LoadStartingWithAsync<AnyDoc>(g.Key.ToLowerInvariant(), pageSize: 100_000)).Count();
    if (got == g.Count()) prefixOk++; else prefixDetail.Add($"{g.Key} {got}/{g.Count()}");
}
Report("L3 LoadStartingWith(lowercase prefix) returns every document of the prefix", prefixOk == prefixes.Count, $"{prefixOk}/{prefixes.Count} prefixes ({string.Join(", ", prefixes.Select(p => p.Key).Take(12))}…) {string.Join("; ", prefixDetail)}");

// L4 reference-field equality ignoring case: the existing index, an auto-index, and explicit Lucene and Corax indexes.
var commitRepos = before.Values.Where(d => (string?)d["@metadata"]!["@collection"] == "Commits").Select(d => (string?)d["Repository"]).Where(r => r is not null).GroupBy(r => r!).ToList();
foreach (var engine in new[] { "Lucene", "Corax" })
{
    var def = new IndexDefinition
    {
        Name = $"Spike/CommitsByRepository/{engine}",
        Maps = { "from c in docs.Commits select new { c.Repository }" },
        Configuration = { ["Indexing.Static.SearchEngineType"] = engine },
    };
    await store.Maintenance.SendAsync(new Raven.Client.Documents.Operations.Indexes.PutIndexesOperation(def));
}
async Task<int> Count(string rql, string value)
{
    using var s = store.OpenAsyncSession();
    return (await s.Advanced.AsyncRawQuery<AnyDoc>(rql).AddParameter("v", value).WaitForNonStaleResults(TimeSpan.FromSeconds(60)).ToListAsync()).Count;
}
foreach (var (name, rql) in new[]
{
    ("existing index Commits/ByRepository", "from index 'Commits/ByRepository' where Repository = $v"),
    ("auto-index", "from Commits where Repository = $v"),
    ("Lucene index", "from index 'Spike/CommitsByRepository/Lucene' where Repository = $v"),
    ("Corax index", "from index 'Spike/CommitsByRepository/Corax' where Repository = $v"),
})
{
    var ok = 0; var detail = new List<string>();
    foreach (var g in commitRepos)
    {
        try
        {
            var orig = await Count(rql, g.Key);
            var lower = await Count(rql, g.Key.ToLowerInvariant());
            var upper = await Count(rql, g.Key.ToUpperInvariant());
            if (orig == g.Count() && lower == orig && upper == orig) ok++; else detail.Add($"{g.Key}: {orig}/{lower}/{upper} of {g.Count()}");
        }
        catch (Exception ex) { detail.Add(ex.GetType().Name + ": " + ex.Message.Split('\n')[0]); break; }
    }
    Report($"L4 reference equality ignores case: {name}", ok == commitRepos.Count, $"{ok}/{commitRepos.Count} repository values (original/lower/upper agree) {string.Join("; ", detail.Take(3))}");
}

// L5 load and save through lowered identities: no duplicate, no id casing change, body unchanged except the lowered reference.
var typed = new (string Collection, Type Type)[]
{
    ("Accounts", typeof(AccountDoc)), ("Repositories", typeof(RepositoryDoc)), ("Commits", typeof(CommitDoc)),
    ("ApiTokens", typeof(ApiTokenDoc)), ("SparkUsers", typeof(SparkUserDoc)), ("UserAccesses", typeof(UserAccessDoc)),
    ("MergeQueueEntries", typeof(MergeQueueEntryDoc)),
};
var touched = 0;
foreach (var (coll, type) in typed)
{
    var ids = before.Where(d => (string?)d.Value["@metadata"]!["@collection"] == coll).Select(d => d.Key.ToLowerInvariant()).ToList();
    foreach (var chunk in ids.Chunk(128))
    {
        foreach (var pass in new[] { 1, 2 })
        {
            var n = await (Task<int>)typeof(Touch).GetMethod(nameof(Touch.Run))!.MakeGenericMethod(type).Invoke(null, [store, chunk, pass])!;
            touched += pass == 2 ? n : 0;
        }
    }
}
var after = await Snapshot();
var dupes = after.Count - before.Count;
var idCasing = after.Keys.Count(k => !before.ContainsKey(k) || before.Keys.First(b => string.Equals(b, k, StringComparison.OrdinalIgnoreCase)) != k);
static JObject Strip(JObject d)
{
    var c = (JObject)d.DeepClone();
    var m = (JObject)c["@metadata"]!;
    foreach (var p in new[] { "@change-vector", "@last-modified", "Raven-Clr-Type", "@flags", "@counters" }) m.Remove(p);
    return c;
}
var bodyDiffs = new Dictionary<string, int>();
var savedCollections = typed.Select(t => t.Collection).ToHashSet();
foreach (var (k, b) in before)
{
    if (!savedCollections.Contains((string)b["@metadata"]!["@collection"]!)) continue;
    var a = after.Keys.First(x => string.Equals(x, k, StringComparison.OrdinalIgnoreCase));
    var bs = Strip(b); var @as = Strip(after[a]);
    foreach (var p in bs.Properties().Select(p => p.Name).Union(@as.Properties().Select(p => p.Name)))
        if (!JToken.DeepEquals(bs[p], @as[p]))
        {
            var lowered = bs[p]?.Type == JTokenType.String && ((string)bs[p]!).ToLowerInvariant() == (string?)@as[p];
            var key = $"{b["@metadata"]!["@collection"]}.{p}{(lowered ? " (lowered)" : "")}";
            bodyDiffs[key] = bodyDiffs.GetValueOrDefault(key) + 1;
        }
}
var flagsDiff = before.Count(kv => after.TryGetValue(kv.Key, out var a) && (string?)kv.Value["@metadata"]!["@flags"] != (string?)a["@metadata"]!["@flags"]);
Report("L5 saved documents", touched > 0, $"{touched} documents loaded and saved twice in 7 collections");
Report("L5 no duplicate document", dupes == 0, $"before {before.Count}, after {after.Count}");
Report("L5 no id casing change", idCasing == 0, $"{idCasing} ids changed casing");
Report("L5 bodies unchanged except lowered references", bodyDiffs.Keys.All(k => k.EndsWith("(lowered)")), bodyDiffs.Count == 0 ? "no differences" : string.Join(", ", bodyDiffs.Select(kv => $"{kv.Key}×{kv.Value}")));
Report("L5 @flags unchanged", flagsDiff == 0, $"{flagsDiff} documents changed @flags");

// L6 after the reference was lowered on half of the documents: index equality still finds both casings.
foreach (var name in new[] { "Spike/CommitsByRepository/Lucene", "Spike/CommitsByRepository/Corax" })
{
    var ok = 0;
    foreach (var g in commitRepos) if (await Count($"from index '{name}' where Repository = $v", g.Key) == g.Count()) ok++;
    Report($"L6 after re-save (all lowered) the original casing still matches: {name}", ok == commitRepos.Count, $"{ok}/{commitRepos.Count}");
}

Console.WriteLine();
Console.WriteLine($"SUMMARY {results.Count(r => r.Ok)}/{results.Count} OK");
foreach (var r in results.Where(r => !r.Ok)) Console.WriteLine($"  FAIL {r.Check}");
File.Delete(dump);
return 0;

public static class Touch
{
    public static async Task<int> Run<T>(IDocumentStore store, string[] ids, int pass) where T : DocBase
    {
        using var s = store.OpenAsyncSession();
        var docs = await s.LoadAsync<T>(ids);
        foreach (var d in docs.Values) { if (d is null) continue; if (pass == 1) d.Rest["__spike"] = 1; else d.Rest.Remove("__spike"); }
        await s.SaveChangesAsync();
        return docs.Values.Count(d => d is not null);
    }
}

[AttributeUsage(AttributeTargets.Class)] public sealed class CollAttribute(string name) : Attribute { public string Name { get; } = name; }

// What the generator emits, minus Id<T>: a lowered identity; every other field kept through extension data.
public abstract class DocBase
{
    [JsonIgnore] public string Id { get; set; } = "";
    [JsonIgnore] public string? __DocumentId { get => Id; set => Id = value?.ToLowerInvariant() ?? ""; }
    [JsonExtensionData] public IDictionary<string, JToken> Rest { get; set; } = new Dictionary<string, JToken>();
}
public sealed class AnyDoc : DocBase;
[Coll("Accounts")] public sealed class AccountDoc : DocBase;
[Coll("Repositories")] public sealed class RepositoryDoc : DocBase;
[Coll("ApiTokens")] public sealed class ApiTokenDoc : DocBase;
[Coll("SparkUsers")] public sealed class SparkUserDoc : DocBase;
[Coll("UserAccesses")] public sealed class UserAccessDoc : DocBase;
[Coll("MergeQueueEntries")] public sealed class MergeQueueEntryDoc : DocBase;
// A reference typed as Id<Repository>: its converter lowers on read, so a save writes it lowercase.
[Coll("Commits")] public sealed class CommitDoc : DocBase
{
    private string? repository;
    public string? Repository { get => repository; set => repository = value?.ToLowerInvariant(); }
}
