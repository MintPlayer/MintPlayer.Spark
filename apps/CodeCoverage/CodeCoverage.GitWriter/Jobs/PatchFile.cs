using System.Text;

namespace CodeCoverage.GitWriter.Jobs;

/// <summary>One line of a hunk: <c>' '</c> context, <c>'-'</c> removed or <c>'+'</c> added, without its marker (a CR of
/// a CRLF file stays in <see cref="Text"/>). <see cref="NoNewline"/>: git's "\ No newline at end of file" follows it.</summary>
public sealed record PatchLine(char Kind, string Text, bool NoNewline);

/// <summary>A hunk with git's 1-based starts and exact counts.</summary>
public sealed record PatchHunk(int OldStart, int OldCount, int NewStart, int NewCount, IReadOnlyList<PatchLine> Lines)
{
    /// <summary>The hunk's change blocks: each run of removed lines and the run of added lines right after it, with the
    /// 1-based line number of the first removed line in the old file, and the lines just before the block (old side).</summary>
    public IEnumerable<ChangeBlock> Blocks()
    {
        var oldLine = OldStart;
        var newLine = NewStart;
        var i = 0;
        while (i < Lines.Count)
        {
            if (Lines[i].Kind == ' ')
            {
                oldLine++;
                newLine++;
                i++;
                continue;
            }
            var start = i;
            var removed = new List<string>();
            var added = new List<string>();
            var firstOld = oldLine;
            var firstNew = newLine;
            while (i < Lines.Count && Lines[i].Kind == '-')
            {
                removed.Add(Lines[i].Text);
                oldLine++;
                i++;
            }
            while (i < Lines.Count && Lines[i].Kind == '+')
            {
                added.Add(Lines[i].Text);
                newLine++;
                i++;
            }
            // A '-' after a '+' run starts the next block.
            var before = Lines.Take(start).Where(l => l.Kind != '+').Select(l => l.Text).ToList();
            yield return new ChangeBlock(firstOld, firstNew, removed, added, before);
        }
    }
}

/// <summary>A run of removed lines and the added lines that replace them.</summary>
/// <param name="Before">The hunk's old-side lines before the block, nearest last.</param>
public sealed record ChangeBlock(int OldLine, int NewLine, IReadOnlyList<string> Removed, IReadOnlyList<string> Added, IReadOnlyList<string> Before);

/// <summary>One <c>diff --git</c> section of a validated patch.</summary>
public sealed record PatchFile(string OldPath, string NewPath, bool IsNew, bool IsDeleted, bool IsRename, IReadOnlyList<PatchHunk> Hunks)
{
    public string Path => NewPath;

    /// <summary>Every added line's text, in patch order.</summary>
    public IEnumerable<string> AddedLines => Hunks.SelectMany(h => h.Lines).Where(l => l.Kind == '+').Select(l => l.Text);

    /// <summary>
    /// The file after the patch, applied in memory to <paramref name="original"/> exactly as written: every context and
    /// removed line must equal the original's line at its position (newline at end of file included). Null when it does
    /// not, which <c>git apply</c> would refuse too.
    /// </summary>
    public string? ApplyTo(string original)
    {
        var lines = new List<(string Text, bool Newline)>();
        var start = 0;
        while (start < original.Length)
        {
            var end = original.IndexOf('\n', start);
            if (end < 0)
            {
                lines.Add((original[start..], false));
                break;
            }
            lines.Add((original[start..end], true));
            start = end + 1;
        }
        var result = new StringBuilder(original.Length + 256);
        var position = 0;
        foreach (var hunk in Hunks)
        {
            var hunkStart = hunk.OldCount == 0 ? hunk.OldStart : hunk.OldStart - 1;
            if (hunkStart < position || hunkStart > lines.Count)
                return null;
            for (; position < hunkStart; position++)
                Append(result, lines[position]);
            foreach (var line in hunk.Lines)
            {
                switch (line.Kind)
                {
                    case ' ' or '-':
                        if (position >= lines.Count || lines[position].Text != line.Text || lines[position].Newline == line.NoNewline)
                            return null;
                        if (line.Kind == ' ')
                            Append(result, lines[position]);
                        position++;
                        break;
                    case '+':
                        Append(result, (line.Text, !line.NoNewline));
                        break;
                }
            }
        }
        for (; position < lines.Count; position++)
            Append(result, lines[position]);
        return result.ToString();
    }

    private static void Append(StringBuilder builder, (string Text, bool Newline) line)
    {
        builder.Append(line.Text);
        if (line.Newline)
            builder.Append('\n');
    }
}
