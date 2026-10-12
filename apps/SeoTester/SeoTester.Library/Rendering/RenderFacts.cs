namespace SeoTester.Rendering;

/// <summary>
/// What the headless browser saw of one page in one viewport (<c>docs/seo_analyzer_PRD.md</c> F12, M10): the
/// rendered DOM's text and links, and the layout facts of the responsiveness checks. Collected in the page by
/// <c>collect.js</c>, so every field is untrusted (the page's own script can tamper with the DOM it reports).
/// </summary>
public sealed record RenderedView
{
    public int ViewportWidth { get; init; }

    /// <summary><c>document.documentElement.scrollWidth</c>: wider than the viewport means horizontal scrolling.</summary>
    public int ScrollWidth { get; init; }

    public string? Title { get; init; }
    public int H1Count { get; init; }

    /// <summary>Words of the body's visible text.</summary>
    public int Words { get; init; }

    /// <summary>Same-site link targets (absolute, without fragment), at most 500.</summary>
    public IReadOnlyList<string> Links { get; init; } = [];

    /// <summary>Characters of visible text, and those set below 12 CSS pixels.</summary>
    public int TextCharacters { get; init; }
    public int SmallTextCharacters { get; init; }

    /// <summary>The images in the document (at most 200).</summary>
    public IReadOnlyList<ImageFact> Images { get; init; } = [];

    /// <summary>The Largest Contentful Paint element's tag and, for an image, its source.</summary>
    public string? LcpTag { get; init; }
    public string? LcpSource { get; init; }
    public bool LcpLazy { get; init; }
}

/// <summary>One image: lazy-loaded or not, and where it sits relative to the first screen.</summary>
public sealed record ImageFact(string? Source, bool Lazy, bool AboveFold);

/// <summary>A request the page made and how it was answered (from the router, not from the page).</summary>
/// <param name="Bytes">The body's bytes after decompression (the router's client decompresses).</param>
/// <param name="Outcome"><c>fulfilled</c>, <c>blocked</c> (refused by the SSRF rules, robots.txt or a sink) or <c>failed</c>.</param>
public sealed record NetworkEntry(string Url, string ResourceType, int Status, long Bytes, bool ThirdParty, string Outcome);

/// <summary>An axe-core violation: the rule, its impact and how many elements failed it.</summary>
/// <param name="Targets">axe's CSS selectors of the first failing elements (at most five), for the finding's evidence.</param>
public sealed record AxeViolation(string Id, string? Impact, int Nodes, string? Help, IReadOnlyList<string>? Targets = null);

/// <summary>JavaScript loaded before the page settled, and the share of it that never ran (Chromium's precise coverage).</summary>
public sealed record ScriptCoverage(long Bytes, long UnusedBytes);

/// <summary>A service worker the page asked to register (registration itself is blocked, §6).</summary>
public sealed record ServiceWorkerFact(string ScriptUrl, int? Status, string? CacheControl, bool HandlesFetch);

/// <summary>Analytics as the browser saw it: which tools loaded, the hits they sent before any interaction, and after a client-side route change.</summary>
/// <param name="RouteChanged">A click on a same-site link changed the URL without loading a document (an SPA route change); null when no link was tried.</param>
public sealed record AnalyticsFact(IReadOnlyList<string> Tools, int HitsOnLoad, bool ConsentManager, bool? RouteChanged, int HitsAfterRouteChange);

/// <summary>Everything the render stage learnt about one sampled page.</summary>
public sealed record RenderedPageFacts
{
    public required string Url { get; init; }

    /// <summary>The HTML as served (the routed document response), parsed without scripts.</summary>
    public RawView? Raw { get; init; }

    /// <summary>The 375 px phone render (axe, layout, network log); null when it failed.</summary>
    public RenderedView? Mobile { get; init; }

    /// <summary>The desktop render (the rendered-vs-raw and mobile-parity comparisons).</summary>
    public RenderedView? Desktop { get; init; }

    public IReadOnlyList<NetworkEntry> Network { get; init; } = [];
    public IReadOnlyList<AxeViolation>? Axe { get; init; }
    public ScriptCoverage? Coverage { get; init; }

    /// <summary>Why the page could not be rendered (navigation failed, timed out), or null.</summary>
    public string? Error { get; init; }
}

/// <summary>The served HTML's text and structure, for the comparison with the rendered DOM.</summary>
public sealed record RawView(string? Title, int H1Count, int Words, int Links);
