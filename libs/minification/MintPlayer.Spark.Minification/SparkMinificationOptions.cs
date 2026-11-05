namespace MintPlayer.Spark.Minification;

/// <summary>
/// Options of <see cref="SparkMinificationExtensions.AddMinification"/> (<c>docs/html_minification_PRD.md</c> G5). Bound
/// from the configuration section <c>Spark:Minification</c> after the code's own <c>configure</c>, so configuration wins:
/// <c>Spark:Minification:Enabled=false</c> turns it off on a running deployment without a rebuild.
/// </summary>
/// <remarks>
/// The rest is fixed, so that a page keeps hydrating: attribute quotes are kept, embedded JSON (the transfer state
/// <c>ng-state</c>, JSON-LD) and inline JavaScript are left as they are, optional end tags stay, and the inline
/// <c>&lt;style&gt;</c> blocks are minified, which is where nearly all of the saving is (PRD SP1). It runs in Development
/// too, so a hydration break shows before production.
/// </remarks>
public sealed class SparkMinificationOptions
{
    /// <summary>The configuration section the options are bound from.</summary>
    public const string SectionName = "Spark:Minification";

    /// <summary>
    /// Minify at all. Default <see langword="true"/>. When off, no middleware is added, and the startup log says so.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Path prefixes that are never minified, matched by whole segments, case-insensitively (<c>/admin</c> covers
    /// <c>/admin/users</c>, not <c>/administrator</c>). Default empty. The escape hatch for a page that does not survive
    /// minification. The development server's paths and the template files are always excluded.
    /// </summary>
    public IList<string> ExcludedPaths { get; } = new List<string>();

    /// <summary>
    /// How hard whitespace between and inside elements is collapsed. Default <see cref="SparkWhitespaceMinification.Safe"/>:
    /// careful with the HTML, which hydration depends on, while the CSS is minified fully either way.
    /// </summary>
    public SparkWhitespaceMinification Whitespace { get; set; } = SparkWhitespaceMinification.Safe;

    /// <summary>
    /// Remove HTML comments. Default <see langword="false"/>.
    /// <para>
    /// ⚠️ <b>This can break Angular hydration.</b> The comments in a server-rendered Angular page are almost all
    /// hydration markers (<c>&lt;!----&gt;</c>, <c>&lt;!--container--&gt;</c>, <c>&lt;!--ngetn--&gt;</c>,
    /// <c>&lt;!--ngtns--&gt;</c>): the compiler already drops template comments. Without them the browser cannot match
    /// the rendered DOM and throws NG0500-NG0503 (https://angular.dev/errors/NG0500), destroys the server's DOM and
    /// renders the page again. Turn it on only for an app that does not hydrate.
    /// </para>
    /// </summary>
    public bool RemoveComments { get; set; }
}

/// <summary>
/// How hard <see cref="SparkMinificationOptions.Whitespace"/> collapses whitespace: WebMarkupMin's modes of the same
/// names, each removing more than the one before. Whitespace inside <c>&lt;pre&gt;</c>, <c>&lt;textarea&gt;</c> and
/// scripts is never touched. On mintplayer.com's pages the three differ by less than 0.1 % (PRD SP1): Angular's own
/// output carries almost no whitespace between elements.
/// </summary>
public enum SparkWhitespaceMinification
{
    /// <summary>Collapses runs of whitespace, keeping one space wherever whitespace could matter. The default.</summary>
    Safe,

    /// <summary>
    /// Also removes whitespace around block elements. A single space Angular kept beside a block element (two
    /// <c>&lt;div&gt;</c>s on one template line) disappears, and hydration would then miss that text node.
    /// </summary>
    Medium,

    /// <summary>
    /// Removes as much whitespace as WebMarkupMin can, including the single spaces between inline elements.
    /// <para>
    /// ⚠️ <b>This breaks Angular hydration.</b> Angular keeps a whitespace-only text node that has no line break
    /// (<c>&lt;b&gt;a&lt;/b&gt; &lt;i&gt;b&lt;/i&gt;</c>) and expects it in the server-rendered DOM; this mode removes
    /// it, and the browser fails with <c>TypeError: Cannot set properties of null (setting 'nodeValue')</c>, then
    /// fetches the page's data again. Measured on every mintplayer.com page (PRD §6 SP2).
    /// </para>
    /// </summary>
    Aggressive,
}
