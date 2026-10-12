using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Mcp;
using MintPlayer.Spark.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SeoTester.Catalogue;
using SeoTester.Checks;
using SeoTester.Crawling;
using SeoTester.Entities;
using SeoTester.LookupReferences;
using SeoTester.Scoring;
using SeoTester.Sites;

namespace SeoTester.Mcp;

/// <summary>
/// SeoTester's MCP tools (<c>docs/spark_mcp_PRD.md</c> §4.7, <c>docs/seotester/mcp.md</c>), prefixed <c>seotester_</c>:
/// the sites, scans and findings of the caller's organizations, the catalogue's how-to-fix, and one write, starting a
/// scan.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only Spark's checked paths</b> (MCP PRD §4.5, analyzer SPARK050): <see cref="IQueryExecutor"/> and the
/// <c>PersistentObject</c> methods of <see cref="IDatabaseAccess"/> for every read, <see cref="ISparkMcpActions"/> for the
/// write. So <c>security.json</c>, the organizations row policy (Viewer reads, Member starts a scan), attribute rights and
/// the agent layers inside <c>IPermissionService</c> decide exactly as on the web, and a site, scan or page the caller may
/// not see answers like one that does not exist (<see cref="SparkMcpResult.Refuse"/>, D17).
/// </para>
/// <para>
/// <b>Untrusted text</b> (D10): every answer is JSON fenced as data (<see cref="SparkMcpResult.Json(string, object?)"/>); page
/// titles, descriptions, URLs, element selectors and a site's own address are written by the site's owner or fetched from the
/// web. Only the application's own notes (what the data is, what to call next) stand outside the fence.
/// </para>
/// <para>
/// <b>Page results</b> (<see cref="PageResult"/>) are not exposed to agents as a type (<c>agentPermissions</c> None: large,
/// expiring); the tools read them as part of their scan (<see cref="McpReadsThroughAttribute"/>), and say so when the
/// 7-day retention has removed them or a later scan replaced them (a site keeps one result per page, the latest), never
/// answering an empty "clean" list (§4.7 gap 2).
/// </para>
/// </remarks>
[McpServerToolType]
public sealed class SeoTools(
    IQueryExecutor queries,
    IQueryLoader queryLoader,
    IModelLoader models,
    IDatabaseAccess database,
    ISparkMcpActions actions,
    IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions,
    TimeProvider time)
{
    private const int MaxSitesPerPage = 100;
    private const int MaxPagesPerCall = 200;
    private const int QueryBatch = 100;

    /// <summary>Room for the note, the fence and the envelope under <see cref="SparkMcpResult.MaxCharacters"/>.</summary>
    private const int Budget = SparkMcpResult.MaxCharacters - 2_000;

    // ---- Sites and scans -------------------------------------------------------------------------------------------

    [McpServerTool(Name = "list_sites", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The websites of your SeoTester organizations: address, whether your organization verified it owns it, the latest score (0-100) and grade (A-F), and when the latest scan ran. Start here; pass a site's id or address to the other tools.")]
    public async Task<CallToolResult> ListSitesAsync(
        [Description("Sites to skip, for paging (use nextSkip from the previous answer).")] int skip = 0,
        [Description("Sites to return, 1 to 100.")] int take = 50,
        CancellationToken cancellationToken = default)
    {
        if (skip < 0 || take is < 1 or > MaxSitesPerPage)
            return Error($"skip must be 0 or more and take between 1 and {MaxSitesPerPage}.");

        QueryResult result;
        try
        {
            result = await queries.ExecuteQueryAsync(Query("GetSites"), skip: skip, take: take, cancellationToken: cancellationToken);
        }
        catch (SparkAccessDeniedException)
        {
            return SparkMcpResult.Refuse();
        }

        var sites = new JsonArray();
        foreach (var item in result.Items)
        {
            var score = Int(Cell(item, nameof(Site.LatestScore)));
            sites.Add(new JsonObject
            {
                ["site"] = item.Id,
                ["origin"] = Str(Cell(item, nameof(Site.Origin))),
                ["verified"] = Cell(item, nameof(Site.IsVerified)) is true or "true" or "True",
                ["score"] = score,
                ["grade"] = score is { } s ? ScoreCalculator.GradeOf(s) : null,
                ["lastScanAt"] = Date(Cell(item, nameof(Site.LatestRunAtUtc))),
            });
        }

        var next = skip + result.Items.Count;
        return SparkMcpResult.Json(
            sites.Count == 0 && skip == 0
                ? "You have no sites in SeoTester yet: add one at the website first."
                : "Your organizations' sites. Pass a site's \"site\" id (or its address) to seotester_run_summary or seotester_list_findings.",
            new JsonObject
            {
                ["totalSites"] = result.TotalItems,
                ["nextSkip"] = next < result.TotalItems ? next : (int?)null,
                ["sites"] = sites,
            });
    }

    [McpServerTool(Name = "run_summary", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("A scan of one of your sites: its score and grade per category, and every check with its rule id, severity, status (Fail, Warn, Pass), number of affected pages and whether it is New, Fixed or Unchanged since the previous scan. Without a scan id: the latest finished scan.")]
    public async Task<CallToolResult> RunSummaryAsync(
        [Description("The site: its id from seotester_list_sites (Sites/…) or its address (example.com).")] string site,
        [Description("A scan id (AuditRuns/…) of that site; empty for the latest finished scan.")] string? run = null,
        CancellationToken cancellationToken = default)
    {
        var target = await ScanAsync(site, run, cancellationToken);
        if (target.Refusal is { } refusal)
            return refusal;
        if (target.Run is not { } scan)
            return SparkMcpResult.Json(NoScanNote(target), Header(target));

        var catalogue = await CatalogueAsync(cancellationToken);
        var checks = new JsonArray();
        foreach (var result in Rows(scan, nameof(AuditRun.CheckResults))
                     .Select(r => (Row: r, Check: Str(Value(r, nameof(RunCheckResult.CheckId))) ?? string.Empty))
                     .OrderBy(r => StatusRank(Str(Value(r.Row, nameof(RunCheckResult.Status)))))
                     .ThenBy(r => SeverityRank(catalogue.Get(r.Check)?.Severity))
                     .ThenBy(r => r.Check, StringComparer.Ordinal))
        {
            var definition = catalogue.Get(result.Check);
            var check = new JsonObject
            {
                ["check"] = result.Check,
                ["title"] = definition?.Title,
                ["category"] = definition?.Category,
                ["severity"] = definition?.Severity,
                ["status"] = Str(Value(result.Row, nameof(RunCheckResult.Status))),
                ["affected"] = Int(Value(result.Row, nameof(RunCheckResult.Affected))),
            };
            if (Str(Value(result.Row, nameof(RunCheckResult.Delta))) is { Length: > 0 } delta)
                check["delta"] = delta;
            if (Str(Value(result.Row, nameof(RunCheckResult.Note))) is { Length: > 0 } note)
                check["note"] = note;
            if (Strings(Value(result.Row, nameof(RunCheckResult.SampleUrls))) is { Count: > 0 } samples)
                check["sampleUrls"] = new JsonArray([.. samples.Select(s => (JsonNode?)s)]);
            checks.Add(check);
        }

        var categories = new JsonArray();
        foreach (var category in Rows(scan, nameof(AuditRun.CategoryScores)))
        {
            categories.Add(new JsonObject
            {
                ["category"] = Str(Value(category, nameof(RunCategoryScore.Category))),
                ["score"] = Int(Value(category, nameof(RunCategoryScore.Score))),
                ["grade"] = Str(Value(category, nameof(RunCategoryScore.Grade))),
                ["cappedByCriticalFailure"] = Value(category, nameof(RunCategoryScore.Capped)) is true,
            });
        }

        var body = Header(target);
        body["categories"] = categories;
        body["checks"] = checks;
        return SparkMcpResult.Json(
            PageRetentionNote(target) + " Per-page findings: seotester_list_findings. How to fix a rule: seotester_explain_check.",
            body);
    }

    [McpServerTool(Name = "list_findings", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [McpReadsThrough(nameof(PageResult), nameof(AuditRun))]
    [Description("The failing and warning checks of a scan, page by page and grouped by rule: the page URL, what is wrong, and where known the elements (CSS selectors). Reads the scan's pages in URL order, 'take' pages per call; page with skip/nextSkip. Filter by severity, category, rule or URL prefix.")]
    public async Task<CallToolResult> ListFindingsAsync(
        [Description("The site: its id from seotester_list_sites (Sites/…) or its address (example.com).")] string site,
        [Description("A scan id (AuditRuns/…) of that site; empty for the latest finished scan.")] string? run = null,
        [Description("Only rules of this severity or worse: Critical, High, Medium, Low or Info.")] string? minSeverity = null,
        [Description("Only rules of this category: Seo, Technical, Performance, Security, Accessibility, Social, Mobile or Domain.")] string? category = null,
        [Description("Only this rule, e.g. seo.title-present.")] string? checkId = null,
        [Description("Only pages whose URL starts with this, e.g. https://example.com/blog/.")] string? urlPrefix = null,
        [Description("Pages of the scan to skip, in URL order (use nextSkip from the previous answer).")] int skip = 0,
        [Description("Pages of the scan to read, 1 to 200.")] int take = 100,
        CancellationToken cancellationToken = default)
    {
        if (skip < 0 || take is < 1 or > MaxPagesPerCall)
            return Error($"skip must be 0 or more and take between 1 and {MaxPagesPerCall}.");
        if (minSeverity is { Length: > 0 } && !Enum.TryParse<CheckSeverity>(minSeverity, ignoreCase: true, out _))
            return Error("minSeverity must be one of Critical, High, Medium, Low, Info.");
        if (category is { Length: > 0 } && !Enum.TryParse<CheckCategory>(category, ignoreCase: true, out _))
            return Error("category must be one of " + string.Join(", ", Enum.GetNames<CheckCategory>()) + ".");

        var target = await ScanAsync(site, run, cancellationToken);
        if (target.Refusal is { } refusal)
            return refusal;
        if (target.Run is not { } scan)
            return SparkMcpResult.Json(NoScanNote(target), Header(target));

        // The rules to report: flagged on this scan (its totals outlive the pages) and inside the filters.
        var catalogue = await CatalogueAsync(cancellationToken);
        var flagged = Rows(scan, nameof(AuditRun.CheckResults))
            .Select(r => (Check: Str(Value(r, nameof(RunCheckResult.CheckId))) ?? string.Empty, Row: r))
            .Where(r => Str(Value(r.Row, nameof(RunCheckResult.Status))) is nameof(CheckStatus.Fail) or nameof(CheckStatus.Warn))
            .Where(r => Matches(catalogue.Get(r.Check), r.Check, minSeverity, category, checkId))
            .GroupBy(r => r.Check, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Row, StringComparer.Ordinal);

        var body = Header(target);
        if (flagged.Count == 0)
        {
            body["findings"] = new JsonArray();
            return SparkMcpResult.Json("No failing or warning check of this scan matches these filters.", body);
        }

        if (target.PagesExpired)
        {
            // §4.7 gap 2: the totals remain, the pages are gone. Never an empty list that reads as "clean".
            body["rules"] = RuleTotals(flagged, catalogue);
            return SparkMcpResult.Json(PageRetentionNote(target), body);
        }

        // The scan's pages in URL order, take at a time; their findings, through the same row-gated reads as the web.
        var runPages = Query("RunPages");
        var summaries = await SummariesAsync(flagged.Keys, cancellationToken);
        var groups = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
        var used = 0;
        var read = 0;
        var total = 0;
        var truncated = false;
        var position = skip;
        while (read < take && !truncated)
        {
            var batch = await queries.ExecuteQueryAsync(runPages, scan, skip: position, take: Math.Min(QueryBatch, take - read), cancellationToken: cancellationToken);
            total = batch.TotalItems;
            if (batch.Items.Count == 0 || position >= total)
                break;

            var ids = batch.Items
                .Where(i => urlPrefix is not { Length: > 0 } || (Str(Cell(i, nameof(PageResult.Url)))?.StartsWith(urlPrefix, StringComparison.OrdinalIgnoreCase) ?? false))
                .Select(i => i.Id)
                .ToList();
            var pages = ids.Count == 0 ? [] : await database.GetPersistentObjectsByIdAsync(Type(nameof(PageResult)), ids);
            var byId = pages.Where(p => p.Id is not null).ToDictionary(p => p.Id!, StringComparer.OrdinalIgnoreCase);

            foreach (var item in batch.Items)
            {
                if (!byId.TryGetValue(item.Id, out var page))
                {
                    position++;
                    read++;
                    continue;
                }

                var url = Str(Value(page, nameof(PageResult.Url)));
                var entries = Findings(page)
                    .Where(f => flagged.ContainsKey(f.Check) && f.Status is nameof(CheckStatus.Fail) or nameof(CheckStatus.Warn))
                    .Select(f => (f.Check, Entry: FindingEntry(url, f, summaries)))
                    .ToList();
                var size = entries.Sum(e => Measure(e.Entry) + 1);
                if (used + size > Budget && used > 0)
                {
                    truncated = true;
                    break;
                }

                foreach (var (check, entry) in entries)
                {
                    if (!groups.TryGetValue(check, out var list))
                        groups[check] = list = [];
                    list.Add(entry);
                }
                used += size;
                position++;
                read++;
            }

            if (position >= total)
                break;
        }

        // A site keeps one result per page, the latest (owner decision 2026-10-12): a page a later scan fetched again holds
        // that scan's result now. With none of this scan's left, its own totals answer, never an empty list that reads as clean.
        var scanned = Int(Value(scan, nameof(AuditRun.PagesDone))) ?? 0;
        if (total == 0)
        {
            body["rules"] = RuleTotals(flagged, catalogue);
            return SparkMcpResult.Json(NoPagesNote(target), body);
        }

        var rules = new JsonArray();
        foreach (var (check, findings) in groups.OrderBy(g => SeverityRank(catalogue.Get(g.Key)?.Severity)).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            var definition = catalogue.Get(check);
            rules.Add(new JsonObject
            {
                ["check"] = check,
                ["title"] = definition?.Title,
                ["severity"] = definition?.Severity,
                ["category"] = definition?.Category,
                ["affectedInScan"] = Int(Value(flagged[check], nameof(RunCheckResult.Affected))),
                ["pages"] = findings,
            });
        }

        body["totalPages"] = total;
        body["pagesRead"] = read;
        body["nextSkip"] = position < total ? position : (int?)null;
        body["rules"] = rules;
        var note = rules.Count == 0
            ? "None of the pages read has a matching finding."
            : "Findings of the pages read, grouped by rule.";
        if (position < total)
            note += $" Pages {skip + 1}–{position} of {total} were read: call again with skip={position} for the next ones.";
        if (target.Later is { } later && total < scanned)
            note += $" {scanned - total} of this scan's {scanned} pages were checked again by a later scan ({later}): SeoTester keeps one "
                + "result per page, the latest, so they are listed under that scan, not here.";
        note += " Some rules (headers, TLS, DNS, sitemap) are judged once for the site: seotester_run_summary lists them. "
            + "How to fix a rule: seotester_explain_check.";
        return SparkMcpResult.Json(note, body);
    }

    [McpServerTool(Name = "get_page", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [McpReadsThrough(nameof(PageResult), nameof(AuditRun))]
    [Description("One scanned page's latest result (SeoTester keeps one per page): status code, load time, title, meta description, H1, Open Graph tags, and its failing and warning checks with the element where known, and the scan that checked it.")]
    public async Task<CallToolResult> GetPageAsync(
        [Description("The site: its id from seotester_list_sites (Sites/…) or its address (example.com).")] string site,
        [Description("The page's URL, as seotester_list_findings shows it.")] string url,
        [Description("A scan id (AuditRuns/…) of that site; empty for the latest finished scan.")] string? run = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048)
            return Error("url is required (at most 2048 characters).");

        var target = await ScanAsync(site, run, cancellationToken);
        if (target.Refusal is { } refusal)
            return refusal;
        if (target.Run is not { } scan)
            return SparkMcpResult.Json(NoScanNote(target), Header(target));

        // The site's one result for the page, the latest, whichever scan wrote it (owner decision 2026-10-12). The stored id
        // hashes the crawl's URL; the agent may pass it as shown or as typed.
        PersistentObject? page = null;
        foreach (var candidate in new[] { url.Trim(), Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed) ? CrawlUrl.Normalize(parsed).AbsoluteUri : null }.Distinct())
        {
            if (candidate is null)
                continue;
            page = await database.GetPersistentObjectAsync(Type(nameof(PageResult)), PageResult.IdFor(target.Site!.Id!, candidate));
            if (page is not null)
                break;
        }
        if (page is null)
            return target.PagesExpired
                ? SparkMcpResult.Json(PageRetentionNote(target), Header(target))
                : Error("SeoTester has no result for that URL of this site: a page's result is kept 7 days after it was checked. "
                    + "seotester_list_findings lists the scanned pages with their URLs as stored.");
        // The scan that wrote it: an id of this application, never text of the site.
        var pageScan = Str(Value(page, nameof(PageResult.RunId))) is { } written && IsId(written, "AuditRuns/") ? written : null;

        var catalogue = await CatalogueAsync(cancellationToken);
        var findings = Findings(page).ToList();
        var flaggedChecks = findings.Where(f => f.Status is nameof(CheckStatus.Fail) or nameof(CheckStatus.Warn)).Select(f => f.Check).ToHashSet(StringComparer.Ordinal);
        var summaries = await SummariesAsync(flaggedChecks, cancellationToken);
        var pageUrl = Str(Value(page, nameof(PageResult.Url)));

        var body = Header(target);
        body["page"] = new JsonObject
        {
            ["url"] = pageUrl,
            ["scan"] = pageScan,
            ["statusCode"] = Int(Value(page, nameof(PageResult.StatusCode))),
            ["depth"] = Int(Value(page, nameof(PageResult.Depth))),
            ["loadMs"] = Int(Value(page, nameof(PageResult.LoadMs))),
            ["title"] = Str(Value(page, nameof(PageResult.Title))),
            ["description"] = Str(Value(page, nameof(PageResult.Description))),
            ["h1"] = Str(Value(page, nameof(PageResult.H1))),
            ["openGraph"] = new JsonObject
            {
                ["title"] = Str(Value(page, nameof(PageResult.OgTitle))),
                ["description"] = Str(Value(page, nameof(PageResult.OgDescription))),
                ["image"] = Str(Value(page, nameof(PageResult.OgImage))),
                ["url"] = Str(Value(page, nameof(PageResult.OgUrl))),
            },
            ["passedChecks"] = findings.Count(f => f.Status == nameof(CheckStatus.Pass)),
            ["findings"] = new JsonArray([.. findings
                .Where(f => flaggedChecks.Contains(f.Check))
                .OrderBy(f => SeverityRank(catalogue.Get(f.Check)?.Severity))
                .Select(f =>
                {
                    var entry = FindingEntry(null, f, summaries);
                    entry["check"] = f.Check;
                    entry["title"] = catalogue.Get(f.Check)?.Title;
                    entry["severity"] = catalogue.Get(f.Check)?.Severity;
                    return (JsonNode)entry;
                })]),
        };
        var note = string.Equals(pageScan, scan.Id, StringComparison.OrdinalIgnoreCase)
            ? "One page of the scan."
            : $"This page's latest result is from scan {pageScan ?? "unknown"}, not from the scan asked for: SeoTester keeps one result per page, the latest.";
        return SparkMcpResult.Json(note + " How to fix a rule: seotester_explain_check.", body);
    }

    [McpServerTool(Name = "explain_check", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("What a rule checks and why it matters, how to fix it, and ready-made snippets for nginx, Apache, IIS and HTML. Works without signing in.")]
    public async Task<CallToolResult> ExplainCheckAsync(
        [Description("The rule id, e.g. seo.title-present or security.hsts.")] string checkId,
        CancellationToken cancellationToken = default)
    {
        if (!IsCheckId(checkId))
            return Error("checkId looks like 'seo.title-present': a category, a dot and lower-case words joined by '-'.");

        PersistentObject? definition;
        try
        {
            definition = await database.GetPersistentObjectAsync(Type(nameof(CheckDefinition)), CheckDefinition.IdFor(checkId));
        }
        catch (SparkAccessDeniedException)
        {
            return SparkMcpResult.Refuse();
        }

        // The catalogue is public reference text (GET /seotester/catalogue lists it to anyone, PRD D32), so an unknown id
        // is said plainly rather than refused: there is nothing to hide, and signing in would not help.
        if (definition is null)
            return Error($"SeoTester has no rule '{checkId}'. seotester_run_summary lists the rules of a scan.");

        var snippets = new JsonObject();
        foreach (var (name, attribute) in new[] { ("nginx", nameof(CheckDefinition.SnippetNginx)), ("apache", nameof(CheckDefinition.SnippetApache)), ("iis", nameof(CheckDefinition.SnippetIis)), ("html", nameof(CheckDefinition.SnippetHtml)) })
            if (Str(Value(definition, attribute)) is { Length: > 0 } snippet)
                snippets[name] = snippet;

        return SparkMcpResult.Json("A rule of SeoTester's catalogue. {placeholders} in the summary are filled per page by seotester_list_findings.", new JsonObject
        {
            ["check"] = Str(Value(definition, nameof(CheckDefinition.CheckId))),
            ["title"] = Str(Value(definition, nameof(CheckDefinition.TitleEn))),
            ["category"] = Str(Value(definition, nameof(CheckDefinition.Category))),
            ["severity"] = Str(Value(definition, nameof(CheckDefinition.Severity))),
            ["weight"] = Int(Value(definition, nameof(CheckDefinition.Weight))),
            ["enabled"] = Value(definition, nameof(CheckDefinition.Enabled)) is not false,
            ["summary"] = Str(Value(definition, nameof(CheckDefinition.SummaryEn))),
            ["howToFix"] = Str(Value(definition, nameof(CheckDefinition.HowToFixEn))),
            ["snippets"] = snippets,
            ["docsUrl"] = Str(Value(definition, nameof(CheckDefinition.DocsUrl))),
        });
    }

    [McpServerTool(Name = "start_scan", Destructive = false, Idempotent = false, OpenWorld = true)]
    [McpRunsAction(nameof(Site), SeoActionNames.StartAudit)]
    [Description("Starts a new scan of one of your sites (or joins the one already running for it), as the Scan button does: it needs the Member role in the site's organization and counts against your scan quota. Answers the scan id; follow it with seotester_scan_status.")]
    public async Task<CallToolResult> StartScanAsync(
        [Description("The site: its id from seotester_list_sites (Sites/…) or its address (example.com).")] string site,
        CancellationToken cancellationToken = default)
    {
        var found = await SiteAsync(site, cancellationToken);
        if (found.Refusal is { } refusal)
            return refusal;

        // The button's own path: StartAudit through the web's gates (right, Member role, disabled actions), then AuditStarter
        // (quotas, the unverified cool-down, joining a running scan). Never a second way to start a scan.
        var outcome = await actions.RunAsync(nameof(Site), SeoActionNames.StartAudit, found.Site!.Id!, cancellationToken);
        switch (outcome.Status)
        {
            case SparkMcpActionStatus.Executed:
                var result = JsonSerializer.SerializeToNode(outcome.Result, jsonOptions.Value.SerializerOptions);
                var runId = result?["runId"]?.GetValue<string>();
                var joined = result?["joined"]?.GetValue<bool>() == true;
                return SparkMcpResult.Json(
                    (joined ? "A scan of this site was already running in your organization; you joined it." : "Scan started.")
                    + " It takes a few minutes: follow it with seotester_scan_status, then read it with seotester_run_summary.",
                    new JsonObject { ["scan"] = runId, ["joined"] = joined });
            case SparkMcpActionStatus.Refused:
                return SparkMcpResult.Refuse();
            case SparkMcpActionStatus.Throttled:
                var wait = outcome.RetryAfter is { } retry ? $" Try again after {Math.Ceiling(retry.TotalMinutes).ToString(CultureInfo.InvariantCulture)} minutes." : string.Empty;
                return Error("Not started: " + SparkMcpResult.Untrusted(outcome.Message) + wait);
            default:
                return Error("Not started: " + SparkMcpResult.Untrusted(outcome.Message ?? "the scan could not be started."));
        }
    }

    [McpServerTool(Name = "scan_status", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Progress of a scan: its status (Queued, Running, Completed, Failed, Cancelled, Blocked), phase, pages done and discovered, and the score once it is done.")]
    public async Task<CallToolResult> ScanStatusAsync(
        [Description("The scan id (AuditRuns/…), as seotester_start_scan or seotester_run_summary answers it.")] string run,
        CancellationToken cancellationToken = default)
    {
        if (!IsId(run, "AuditRuns/"))
            return SparkMcpResult.Refuse();

        PersistentObject? scan;
        try
        {
            scan = await database.GetPersistentObjectAsync(Type(nameof(AuditRun)), run);
        }
        catch (SparkAccessDeniedException)
        {
            return SparkMcpResult.Refuse();
        }
        if (scan is null)
            return SparkMcpResult.Refuse();

        var status = Str(Value(scan, nameof(AuditRun.Status)));
        var done = status is not null && AuditRunStatus.Finished.Contains(status);
        var body = new JsonObject
        {
            ["scan"] = scan.Id,
            ["origin"] = Str(Value(scan, nameof(AuditRun.Origin))),
            ["status"] = status,
            ["phase"] = Str(Value(scan, nameof(AuditRun.Phase))),
            ["pagesDone"] = Int(Value(scan, nameof(AuditRun.PagesDone))),
            ["pagesDiscovered"] = Int(Value(scan, nameof(AuditRun.PagesDiscovered))),
            ["maxPages"] = Int(Value(scan, nameof(AuditRun.MaxPages))),
            ["startedAt"] = Date(Value(scan, nameof(AuditRun.StartedAtUtc))),
            ["completedAt"] = Date(Value(scan, nameof(AuditRun.CompletedAtUtc))),
            ["score"] = Int(Value(scan, nameof(AuditRun.Score))),
        };
        if (Int(Value(scan, nameof(AuditRun.Score))) is { } score)
            body["grade"] = ScoreCalculator.GradeOf(score);
        return SparkMcpResult.Json(
            done ? "The scan is finished: read it with seotester_run_summary." : "The scan is still running: ask again in a minute.",
            body);
    }

    // ---- Resolving the site and the scan -------------------------------------------------------------------------------

    private sealed record SiteLookup(PersistentObject? Site, CallToolResult? Refusal);

    /// <summary>
    /// The scan a tool reads: the site, the run (null when there is none yet), how long its pages are kept, and the site's
    /// newest scan started after it (<see cref="Later"/>), whose fetches replace this scan's page results (one per page).
    /// </summary>
    private sealed record ScanTarget(PersistentObject? Site, PersistentObject? Run, PersistentObject? Running, DateTime? PagesKeptUntil, bool PagesExpired, CallToolResult? Refusal, string? Later = null);

    /// <summary>
    /// The caller's site by id or address, through the row-gated read: another organization's site, an unknown one and a
    /// malformed argument answer alike (D17).
    /// </summary>
    private async Task<SiteLookup> SiteAsync(string site, CancellationToken cancellationToken)
    {
        try
        {
            if (IsId(site, "Sites/"))
                return await database.GetPersistentObjectAsync(Type(nameof(Site)), site.Trim()) is { } byId
                    ? new(byId, null)
                    : new(null, SparkMcpResult.Refuse());

            if (site is null || !SiteOrigin.TryNormalize(site, out var origin, out var host, out _))
                return new(null, SparkMcpResult.Refuse());

            // By address: the caller's own sites, matched in memory (an exact origin, else the host when no scheme was given).
            var explicitScheme = site.Contains("://", StringComparison.Ordinal);
            var query = Query("GetSites");
            for (var skip = 0; skip < 1_000; skip += QueryBatch)
            {
                var page = await queries.ExecuteQueryAsync(query, skip: skip, take: QueryBatch, cancellationToken: cancellationToken);
                var match = page.Items.FirstOrDefault(i => string.Equals(Str(Cell(i, nameof(Site.Origin))), origin, StringComparison.OrdinalIgnoreCase))
                    ?? (explicitScheme ? null : page.Items.FirstOrDefault(i => Uri.TryCreate(Str(Cell(i, nameof(Site.Origin))), UriKind.Absolute, out var u) && string.Equals(u.Host, host, StringComparison.OrdinalIgnoreCase)));
                if (match is not null)
                    return await database.GetPersistentObjectAsync(Type(nameof(Site)), match.Id) is { } byOrigin
                        ? new(byOrigin, null)
                        : new(null, SparkMcpResult.Refuse());
                if (page.Items.Count < QueryBatch)
                    break;
            }
            return new(null, SparkMcpResult.Refuse());
        }
        catch (SparkAccessDeniedException)
        {
            return new(null, SparkMcpResult.Refuse());
        }
    }

    /// <summary>The site and the scan: the one named (of that site), else the latest finished one, and any scan still running.</summary>
    private async Task<ScanTarget> ScanAsync(string site, string? run, CancellationToken cancellationToken)
    {
        var found = await SiteAsync(site, cancellationToken);
        if (found.Refusal is { } refusal)
            return new(null, null, null, null, false, refusal);
        var siteObject = found.Site!;

        try
        {
            PersistentObject? scan = null;
            PersistentObject? running = null;
            // The site's scans, newest first (Custom.SiteRuns).
            var recent = await queries.ExecuteQueryAsync(Query("SiteRuns"), siteObject, take: 20, cancellationToken: cancellationToken);
            if (!string.IsNullOrWhiteSpace(run))
            {
                // A scan of another site, or one the caller may not see, is refused like an unknown one.
                scan = IsId(run, "AuditRuns/") ? await database.GetPersistentObjectAsync(Type(nameof(AuditRun)), run.Trim()) : null;
                if (scan is null || !string.Equals(Str(Value(scan, nameof(AuditRun.SiteId))), siteObject.Id, StringComparison.OrdinalIgnoreCase))
                    return new(null, null, null, null, false, SparkMcpResult.Refuse());
            }
            else
            {
                // The newest finished scan, and one still running if any.
                var newestRunning = recent.Items.FirstOrDefault(i => Str(Cell(i, nameof(AuditRun.Status))) is { } s && AuditRunStatus.InProgress.Contains(s));
                var newestFinished = recent.Items.FirstOrDefault(i => Str(Cell(i, nameof(AuditRun.Status))) == AuditRunStatus.Completed)
                    ?? recent.Items.FirstOrDefault(i => Str(Cell(i, nameof(AuditRun.Status))) is { } s && AuditRunStatus.Finished.Contains(s));
                if (newestRunning is not null)
                    running = await database.GetPersistentObjectAsync(Type(nameof(AuditRun)), newestRunning.Id);
                if (newestFinished is not null)
                    scan = await database.GetPersistentObjectAsync(Type(nameof(AuditRun)), newestFinished.Id);
            }

            if (scan is null)
                return new(siteObject, null, running, null, false, null);

            // Pages are kept 7 days from when they were written (PageAnalyzer.Retention, the model's expiresAfter); a
            // scan's last page is written by its end, so its pages are gone 7 days after it completed.
            var ended = DateValue(Value(scan, nameof(AuditRun.CompletedAtUtc))) ?? DateValue(Value(scan, nameof(AuditRun.StartedAtUtc)));
            DateTime? keptUntil = ended is { } e ? e + Crawler.PageAnalyzer.Retention : null;
            var expired = keptUntil is { } until && until <= time.GetUtcNow().UtcDateTime;

            // A site keeps one result per page, the latest: the newest scan started after this one replaces the results of
            // the pages it fetches again (the query is newest first, so only its first row can be later).
            var started = DateValue(Value(scan, nameof(AuditRun.StartedAtUtc)));
            var newest = recent.Items.FirstOrDefault();
            var later = newest is not null && !string.Equals(newest.Id, scan.Id, StringComparison.OrdinalIgnoreCase)
                        && DateValue(Cell(newest, nameof(AuditRun.StartedAtUtc))) is { } newestStarted && started is { } s && newestStarted > s
                ? newest.Id
                : null;
            return new(siteObject, scan, running, keptUntil, expired, null, later);
        }
        catch (SparkAccessDeniedException)
        {
            return new(null, null, null, null, false, SparkMcpResult.Refuse());
        }
    }

    private static JsonObject Header(ScanTarget target)
    {
        var header = new JsonObject
        {
            ["site"] = target.Site?.Id,
            ["origin"] = target.Site is null ? null : Str(Value(target.Site, nameof(Site.Origin))),
        };
        if (target.Run is { } run)
        {
            header["scan"] = new JsonObject
            {
                ["id"] = run.Id,
                ["status"] = Str(Value(run, nameof(AuditRun.Status))),
                ["startedAt"] = Date(Value(run, nameof(AuditRun.StartedAtUtc))),
                ["completedAt"] = Date(Value(run, nameof(AuditRun.CompletedAtUtc))),
                ["score"] = Int(Value(run, nameof(AuditRun.Score))),
                ["grade"] = Int(Value(run, nameof(AuditRun.Score))) is { } score ? ScoreCalculator.GradeOf(score) : null,
                ["pagesScanned"] = Int(Value(run, nameof(AuditRun.PagesDone))),
                ["verifiedSite"] = Value(run, nameof(AuditRun.IsVerified)) is true,
                ["fullCrawl"] = Value(run, nameof(AuditRun.IsFullCrawl)) is true,
                ["pageDetailsKeptUntil"] = target.PagesKeptUntil?.ToString("O", CultureInfo.InvariantCulture),
            };
        }
        if (target.Running is { } running)
        {
            header["running"] = new JsonObject
            {
                ["id"] = running.Id,
                ["status"] = Str(Value(running, nameof(AuditRun.Status))),
                ["phase"] = Str(Value(running, nameof(AuditRun.Phase))),
            };
        }
        return header;
    }

    private static string NoScanNote(ScanTarget target)
        => target.Running is not null
            ? "This site's first scan is still running: follow it with seotester_scan_status."
            : "This site has no finished scan yet: start one with seotester_start_scan.";

    /// <summary>The 7-day wording (§4.7 gap 2), in the application's own words: dates from the scan, never site text.</summary>
    private static string PageRetentionNote(ScanTarget target)
    {
        var until = target.PagesKeptUntil?.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        var running = target.Running is not null ? " A newer scan is running: follow it with seotester_scan_status." : string.Empty;
        return target.PagesExpired
            ? $"The per-page findings of this scan expired on {until}: SeoTester keeps a page's latest result for 7 days after it was checked. "
              + "The totals per rule remain (affected pages and up to five sample URLs) and are not a clean result. "
              + "Start a new scan with seotester_start_scan for page-level findings." + running
            : $"Per-page findings of this scan are kept until {until} (7 days after the scan), unless a later scan checks the page "
              + "again: SeoTester keeps one result per page, the latest." + running;
    }

    /// <summary>
    /// A scan none of whose page results is left (§4.7 gap 2, owner decision 2026-10-12): a later scan fetched its pages again,
    /// or none was stored. Its totals answer; the note never reads as a clean result.
    /// </summary>
    private static string NoPagesNote(ScanTarget target)
        => (target.Later is { } later
               ? $"The per-page findings of this scan were replaced: SeoTester keeps one result per page, the latest, and a later scan "
                 + $"({later}) checked these pages again. Read that scan with seotester_list_findings (run = {later}). "
               : "No page result of this scan is stored. ")
           + "The totals per rule remain (affected pages and up to five sample URLs) and are not a clean result.";

    // ---- Findings ------------------------------------------------------------------------------------------------------

    private sealed record FindingView(string Check, string? Status, string? Detail, IReadOnlyList<string> Selectors);

    private static IEnumerable<FindingView> Findings(PersistentObject page)
        => Rows(page, nameof(PageResult.Findings)).Select(f => new FindingView(
            Str(Value(f, nameof(PageFinding.CheckId))) ?? string.Empty,
            Str(Value(f, nameof(PageFinding.Status))),
            Str(Value(f, nameof(PageFinding.Detail))),
            Strings(Value(f, nameof(PageFinding.Selectors)))));

    /// <summary>One finding as the agent reads it: the catalogue's sentence filled from <c>Detail</c>, and the elements' selectors.</summary>
    private static JsonObject FindingEntry(string? url, FindingView finding, IReadOnlyDictionary<string, string> summaries)
    {
        var entry = new JsonObject();
        if (url is not null)
            entry["url"] = url;
        entry["status"] = finding.Status;
        var detail = CheckFinding.ParseDetail(finding.Detail);
        if (summaries.TryGetValue(finding.Check, out var summary) && summary.Length > 0)
            entry["message"] = Fill(summary, detail);
        else if (detail is { Count: > 0 })
            entry["detail"] = new JsonObject([.. detail.Select(d => KeyValuePair.Create(d.Key, (JsonNode?)d.Value))]);
        if (finding.Selectors.Count > 0)
            entry["selectors"] = new JsonArray([.. finding.Selectors.Select(s => (JsonNode?)s)]);
        return entry;
    }

    /// <summary>The rules' totals when the pages are gone: what the scan document keeps for good.</summary>
    private static JsonArray RuleTotals(IReadOnlyDictionary<string, PersistentObject> flagged, CatalogueView catalogue)
        => new([.. flagged
            .OrderBy(f => SeverityRank(catalogue.Get(f.Key)?.Severity)).ThenBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => (JsonNode)new JsonObject
            {
                ["check"] = f.Key,
                ["title"] = catalogue.Get(f.Key)?.Title,
                ["severity"] = catalogue.Get(f.Key)?.Severity,
                ["status"] = Str(Value(f.Value, nameof(RunCheckResult.Status))),
                ["affectedPages"] = Int(Value(f.Value, nameof(RunCheckResult.Affected))),
                ["sampleUrls"] = new JsonArray([.. Strings(Value(f.Value, nameof(RunCheckResult.SampleUrls))).Select(s => (JsonNode?)s)]),
            })]);

    /// <summary><c>{name}</c> placeholders filled from <paramref name="detail"/>; an unknown one is left as written.</summary>
    internal static string Fill(string template, IReadOnlyDictionary<string, string>? detail)
    {
        if (detail is null || detail.Count == 0)
            return template;
        var text = template;
        foreach (var (key, value) in detail)
            text = text.Replace("{" + key + "}", value, StringComparison.Ordinal);
        return text;
    }

    private static bool Matches(CatalogueEntry? definition, string check, string? minSeverity, string? category, string? checkId)
    {
        if (checkId is { Length: > 0 } && !string.Equals(check, checkId.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        if (category is { Length: > 0 } && !string.Equals(definition?.Category, category.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        if (minSeverity is { Length: > 0 } && Enum.TryParse<CheckSeverity>(minSeverity, ignoreCase: true, out var min)
            && SeverityRank(definition?.Severity) > (int)min)
            return false;
        return true;
    }

    // ---- The catalogue -------------------------------------------------------------------------------------------------

    private sealed record CatalogueEntry(string? Title, string? Category, string? Severity);

    private sealed class CatalogueView(Dictionary<string, CatalogueEntry> entries)
    {
        public CatalogueEntry? Get(string checkId) => entries.TryGetValue(checkId, out var entry) ? entry : null;
    }

    /// <summary>The catalogue's titles, categories and severities, through its query (every signed-in user reads it, D32).</summary>
    private async Task<CatalogueView> CatalogueAsync(CancellationToken cancellationToken)
    {
        var entries = new Dictionary<string, CatalogueEntry>(StringComparer.Ordinal);
        var query = Query("GetCheckDefinitions");
        for (var skip = 0; skip < 2_048; skip += QueryBatch)
        {
            var page = await queries.ExecuteQueryAsync(query, skip: skip, take: QueryBatch, cancellationToken: cancellationToken);
            foreach (var item in page.Items)
                if (Str(Cell(item, nameof(CheckDefinition.CheckId))) is { Length: > 0 } id)
                    entries[id] = new CatalogueEntry(Str(Cell(item, nameof(CheckDefinition.TitleEn))), Str(Cell(item, nameof(CheckDefinition.Category))), Str(Cell(item, nameof(CheckDefinition.Severity))));
            if (page.Items.Count < QueryBatch)
                break;
        }
        return new CatalogueView(entries);
    }

    /// <summary>The English summary sentences of <paramref name="checkIds"/>, through the row-gated object read.</summary>
    private async Task<IReadOnlyDictionary<string, string>> SummariesAsync(IEnumerable<string> checkIds, CancellationToken cancellationToken)
    {
        var ids = checkIds.Where(IsCheckId).Select(CheckDefinition.IdFor).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
            return new Dictionary<string, string>();
        var definitions = await database.GetPersistentObjectsByIdAsync(Type(nameof(CheckDefinition)), ids);
        return definitions
            .Select(d => (Id: Str(Value(d, nameof(CheckDefinition.CheckId))), Summary: Str(Value(d, nameof(CheckDefinition.SummaryEn)))))
            .Where(d => d.Id is not null && d.Summary is not null)
            .GroupBy(d => d.Id!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Summary!, StringComparer.Ordinal);
    }

    // ---- Small helpers -------------------------------------------------------------------------------------------------

    private SparkQuery Query(string name)
        => queryLoader.GetQueryByName(name) ?? throw new InvalidOperationException($"SeoTester's model has no query '{name}'.");

    private Guid Type(string name)
        => models.GetEntityTypeByName(name)?.Id ?? throw new InvalidOperationException($"SeoTester's model has no type '{name}'.");

    private int Measure(JsonNode node) => JsonSerializer.Serialize(node, jsonOptions.Value.SerializerOptions).Length;

    private static CallToolResult Error(string message)
    {
        var result = SparkMcpResult.Text(message);
        result.IsError = true;
        return result;
    }

    private static object? Cell(QueryResultItem item, string column)
        => item.Values.FirstOrDefault(v => string.Equals(v.Key, column, StringComparison.Ordinal))?.Value;

    private static object? Value(PersistentObject po, string attribute)
        => po.TryGetAttribute(attribute, out var a) ? a.Value : null;

    private static IReadOnlyList<PersistentObject> Rows(PersistentObject po, string attribute)
        => po.TryGetAttribute(attribute, out var a) && a is PersistentObjectAttributeAsDetail { Objects: { } rows } ? rows : [];

    private static string? Str(object? value) => value switch
    {
        null => null,
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static int? Int(object? value) => value switch
    {
        null => null,
        int i => i,
        long l => (int)l,
        double d => (int)Math.Round(d),
        decimal m => (int)Math.Round(m),
        JsonElement { ValueKind: JsonValueKind.Number } e => e.TryGetInt32(out var i) ? i : (int)Math.Round(e.GetDouble()),
        string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    private static DateTime? DateValue(object? value) => value switch
    {
        DateTime d => d.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : d.ToUniversalTime(),
        DateTimeOffset o => o.UtcDateTime,
        JsonElement { ValueKind: JsonValueKind.String } e when e.TryGetDateTime(out var d) => d.ToUniversalTime(),
        string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) => d,
        _ => null,
    };

    private static string? Date(object? value) => DateValue(value)?.ToString("O", CultureInfo.InvariantCulture);

    private static IReadOnlyList<string> Strings(object? value) => value switch
    {
        null => [],
        string s => [s],
        IEnumerable<string> list => [.. list],
        JsonElement { ValueKind: JsonValueKind.Array } e => [.. e.EnumerateArray().Select(x => x.ToString())],
        System.Collections.IEnumerable items => [.. items.Cast<object?>().Select(Str).Where(s => s is not null).Select(s => s!)],
        _ => [],
    };

    private static int StatusRank(string? status) => status switch
    {
        nameof(CheckStatus.Fail) => 0,
        nameof(CheckStatus.Warn) => 1,
        nameof(CheckStatus.Error) => 2,
        nameof(CheckStatus.Pass) => 3,
        _ => 4,
    };

    private static int SeverityRank(string? severity)
        => Enum.TryParse<CheckSeverity>(severity, ignoreCase: true, out var parsed) ? (int)parsed : (int)CheckSeverity.Info + 1;

    private static bool IsId(string? value, string prefix)
        => value is { Length: > 0 and <= 200 } && value.Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary><c>{category}.{kebab-case}</c>, as the catalogue's ids are (<see cref="CheckDefinition.CheckId"/>).</summary>
    private static bool IsCheckId(string? value)
        => value is { Length: > 2 and <= 100 } && value.Contains('.') && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');
}
