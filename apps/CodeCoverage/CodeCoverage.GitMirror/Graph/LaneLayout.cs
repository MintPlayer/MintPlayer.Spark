namespace CodeCoverage.GitMirror.Graph;

/// <summary>One laid-out row. Its edges live in the snapshot's flat edge list from <see cref="EdgeStart"/>:
/// <see cref="UpCount"/> columns converging into the commit, then <see cref="DownCount"/> (column, colour) pairs, one per
/// parent edge leaving it.</summary>
public struct RowLayout
{
    public int Lane, Colour, EdgeStart, UpCount, DownCount, Width;
}

/// <summary>
/// The lane layout (PRD §4.3, D27 "JoinLeft"): one streaming, top-down pass in date order, so the lanes of row i depend
/// only on rows 0..i and a page needs the layout only as far as the viewer scrolled.
/// <list type="bullet">
/// <item>A commit no lane expects is a tip: it gets the lowest free column and a new colour.</item>
/// <item>Lanes expecting a commit converge into it; it takes the first-parent lane among them (else the leftmost), the
/// others are freed.</item>
/// <item>When the commit's first parent is already expected by another lane, the commit joins that lane only if it lies
/// to the LEFT; otherwise its own lane stays straight and both converge at the parent's row. A mainline in a low column
/// therefore never bends (vscode's 84k-commit main stays in one lane; git's join-at-once zig-zags it 6,068 times).</item>
/// <item>Every further parent joins the lowest lane already expecting it, or opens a new lane with a new colour.</item>
/// <item>Freed columns are reused lowest-first.</item>
/// </list>
/// Written from the description of the straight-branch algorithm (Vigier) and the S5 spike; no code from VS Code Git
/// Graph, whose licence forbids derivative works.
/// </summary>
public sealed class LaneLayout
{
    private const byte FirstParent = 1;

    private readonly CommitStore store;
    private int[] expect = new int[16]; // commit id the lane waits for, or -1
    private int[] colour = new int[16];
    private byte[] flags = new byte[16];
    private int[] nextSame = new int[16]; // singly linked list of the lanes expecting the same commit
    private readonly Dictionary<int, int> head = [];
    private readonly PriorityQueue<int, int> free = new();
    private readonly List<int> ups = [];
    private int width; // columns ever allocated (high-water mark)
    private int nextColour;
    private int topHighest = -1; // highest occupied column at the top of the next row

    public LaneLayout(CommitStore store)
    {
        this.store = store;
        Array.Fill(expect, -1);
    }

    /// <summary>The number of rows laid out so far.</summary>
    public int Row { get; private set; }

    /// <summary>Lays out the next row: commit <paramref name="id"/> with <paramref name="parents"/>, appending its edges
    /// to <paramref name="edges"/>.</summary>
    public RowLayout Step(int id, ReadOnlySpan<int> parents, List<int> edges)
    {
        var row = new RowLayout { EdgeStart = edges.Count };

        // 1. The lanes converging into this commit (the top half of the row).
        ups.Clear();
        if (head.Remove(id, out var first))
        {
            for (var l = first; l >= 0; l = nextSame[l])
                ups.Add(l);
        }
        ups.Sort();

        int lane, col;
        if (ups.Count == 0)
        {
            lane = Alloc();
            col = nextColour++;
        }
        else
        {
            lane = -1;
            foreach (var u in ups)
            {
                if ((flags[u] & FirstParent) != 0)
                {
                    lane = u;
                    break;
                }
            }
            if (lane < 0)
                lane = ups[0];
            col = colour[lane];
            foreach (var u in ups)
            {
                edges.Add(u);
                if (u != lane)
                    Release(u);
            }
        }
        row.Lane = lane;
        row.Colour = col;
        row.UpCount = ups.Count;

        // The commit's own lane is re-registered below (or freed); drop its old expectation first.
        expect[lane] = -1;
        flags[lane] = 0;

        // 2. The edges to the parents (the bottom half of the row).
        var downs = 0;
        if (parents.Length == 0)
        {
            free.Enqueue(lane, lane);
        }
        else
        {
            var joined = LowestExpecting(parents[0]);
            if (joined > lane)
                joined = -1; // only bend left; a lane to the right converges later, at the parent's row
            if (joined >= 0)
            {
                free.Enqueue(lane, lane);
                flags[joined] |= FirstParent;
                edges.Add(joined);
                edges.Add(colour[joined]);
            }
            else
            {
                Expect(lane, parents[0], col, FirstParent);
                edges.Add(lane);
                edges.Add(col);
            }
            downs++;
            for (var k = 1; k < parents.Length; k++)
            {
                var target = LowestExpecting(parents[k]);
                if (target < 0)
                {
                    target = Alloc();
                    Expect(target, parents[k], nextColour++, 0);
                }
                edges.Add(target);
                edges.Add(colour[target]);
                downs++;
            }
        }
        row.DownCount = downs;

        var bottomHighest = width - 1;
        while (bottomHighest >= 0 && expect[bottomHighest] < 0)
            bottomHighest--;
        row.Width = Math.Max(Math.Max(topHighest, bottomHighest), lane) + 1;
        topHighest = bottomHighest;
        Row++;
        return row;
    }

    /// <summary>The lanes occupied at the top edge of the next row, as (column, colour), left to right.</summary>
    public void ActiveLanes(List<(int Column, int Colour)> into)
    {
        into.Clear();
        for (var c = 0; c < width; c++)
        {
            if (expect[c] >= 0)
                into.Add((c, colour[c]));
        }
    }

    private int Alloc()
    {
        if (free.Count > 0)
            return free.Dequeue();
        if (width == expect.Length)
            Grow(width * 2);
        return width++;
    }

    private void Grow(int size)
    {
        var old = expect.Length;
        Array.Resize(ref expect, size);
        Array.Fill(expect, -1, old, size - old);
        Array.Resize(ref colour, size);
        Array.Resize(ref flags, size);
        Array.Resize(ref nextSame, size);
    }

    private void Release(int lane)
    {
        expect[lane] = -1;
        flags[lane] = 0;
        free.Enqueue(lane, lane);
    }

    private void Expect(int lane, int commit, int col, byte fl)
    {
        expect[lane] = commit;
        colour[lane] = col;
        flags[lane] = fl;
        nextSame[lane] = head.TryGetValue(commit, out var h) ? h : -1;
        head[commit] = lane;
    }

    private int LowestExpecting(int commit)
    {
        if (!head.TryGetValue(commit, out var l))
            return -1;
        var min = l;
        for (l = nextSame[l]; l >= 0; l = nextSame[l])
        {
            if (l < min)
                min = l;
        }
        return min;
    }

    // ---- checkpoints ----
    // Row, next colour, width, then per column a flags byte (bit 7 = occupied, bit 0 = first-parent) and, when occupied,
    // the expected commit's 20-byte SHA and its colour. Free columns and the per-commit lane lists are derived on restore,
    // so the lowest-free policy resumes identically. 0.8-1.9 KB each on vscode (D27).

    public byte[] Checkpoint()
    {
        var ms = new MemoryStream();
        WriteVar(ms, Row);
        WriteVar(ms, nextColour);
        WriteVar(ms, width);
        WriteVar(ms, topHighest + 1);
        Span<byte> sha = stackalloc byte[20];
        for (var c = 0; c < width; c++)
        {
            if (expect[c] < 0)
            {
                ms.WriteByte(0);
                continue;
            }
            ms.WriteByte((byte)(0x80 | flags[c]));
            Convert.FromHexString(store.Sha(expect[c]), sha, out _, out _);
            ms.Write(sha);
            WriteVar(ms, colour[c]);
        }
        return ms.ToArray();
    }

    public static LaneLayout Restore(CommitStore store, byte[] data)
    {
        var layout = new LaneLayout(store);
        var p = 0;
        layout.Row = ReadVar(data, ref p);
        layout.nextColour = ReadVar(data, ref p);
        var w = ReadVar(data, ref p);
        layout.topHighest = ReadVar(data, ref p) - 1;
        if (layout.expect.Length < w)
            layout.Grow(Math.Max(w, layout.expect.Length * 2));
        layout.width = w;
        for (var c = 0; c < w; c++)
        {
            var f = data[p++];
            if ((f & 0x80) == 0)
            {
                layout.free.Enqueue(c, c);
                continue;
            }
            var sha = Convert.ToHexStringLower(data, p, 20);
            p += 20;
            var col = ReadVar(data, ref p);
            layout.Expect(c, store.Intern(sha), col, (byte)(f & 0x7f));
        }
        return layout;
    }

    private static void WriteVar(Stream s, int v)
    {
        var u = (uint)v;
        while (u >= 0x80)
        {
            s.WriteByte((byte)(u | 0x80));
            u >>= 7;
        }
        s.WriteByte((byte)u);
    }

    private static int ReadVar(byte[] d, ref int p)
    {
        uint r = 0;
        var shift = 0;
        while (true)
        {
            var b = d[p++];
            r |= (uint)(b & 0x7f) << shift;
            if (b < 0x80)
                return (int)r;
            shift += 7;
        }
    }
}
