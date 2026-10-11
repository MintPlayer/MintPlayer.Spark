using System.Collections;

namespace SeoTester.Rendering;

/// <summary>One range of V8's precise block coverage (<c>Profiler.takePreciseCoverage</c>, <c>detailed: true</c>).</summary>
public readonly record struct CoverageRange(int Start, int End, int Count);

/// <summary>
/// The unused share of a script from V8's precise coverage (<c>performance.code-splitting</c>, M10). V8's ranges nest:
/// the script's own top-level function spans all of it, and an inner range overrides the outer one's count. Painting
/// them outermost first leaves each character with its innermost count, as Puppeteer's coverage does.
/// </summary>
public static class CoverageMath
{
    /// <summary>Most characters of one script considered; a longer script is judged on its first 8 Mi characters.</summary>
    public const int MaxLength = 8 * 1024 * 1024;

    /// <summary>The script's length (the widest range) and its characters that never ran.</summary>
    public static (int Length, int Unused) Measure(IReadOnlyCollection<CoverageRange> ranges)
    {
        if (ranges.Count == 0)
            return (0, 0);
        var length = Math.Min(MaxLength, ranges.Max(r => r.End));
        if (length <= 0)
            return (0, 0);
        var used = new BitArray(length);
        foreach (var range in ranges.OrderBy(r => r.Start).ThenByDescending(r => r.End))
        {
            var start = Math.Clamp(range.Start, 0, length);
            var end = Math.Clamp(range.End, start, length);
            var value = range.Count > 0;
            for (var i = start; i < end; i++)
                used[i] = value;
        }
        var unused = 0;
        for (var i = 0; i < length; i++)
            if (!used[i])
                unused++;
        return (length, unused);
    }
}
