using System.Text;

namespace CodeCoverage.GitMirror.Graph;

/// <summary>Commits interned to dense ids in first-seen order (deterministic for a given stream), with the rows read so
/// far in date order. Parents are interned before their own row arrives.</summary>
public sealed class CommitStore
{
    private readonly Dictionary<string, int> ids = new(StringComparer.Ordinal);
    private readonly List<string> shas = [];
    private readonly List<int[]?> parents = [];
    private readonly List<int> rowOf = []; // id -> row, -1 until its row is read
    private readonly List<int> rows = []; // row -> id
    private readonly List<CommitText> text = []; // row -> text

    public int Count => rows.Count;

    public int Intern(string sha)
    {
        if (ids.TryGetValue(sha, out var id))
            return id;
        id = shas.Count;
        ids.Add(sha, id);
        shas.Add(sha);
        parents.Add(null);
        rowOf.Add(-1);
        return id;
    }

    public string Sha(int id) => shas[id];

    public int IdAt(int row) => rows[row];

    public string ShaAt(int row) => shas[rows[row]];

    public int[] ParentsAt(int row) => parents[rows[row]] ?? [];

    public CommitText TextAt(int row) => text[row];

    /// <summary>The row of a commit that has been read, else -1.</summary>
    public int RowOf(string sha) => ids.TryGetValue(sha, out var id) ? rowOf[id] : -1;

    /// <summary>The first row read whose SHA starts with <paramref name="prefix"/>, else -1.</summary>
    public int RowOfPrefix(string prefix)
    {
        if (prefix.Length == 40)
            return RowOf(prefix);
        for (var r = 0; r < rows.Count; r++)
        {
            if (shas[rows[r]].StartsWith(prefix, StringComparison.Ordinal))
                return r;
        }
        return -1;
    }

    /// <summary>Adds the next row and returns an estimate of the bytes it holds (D22: text included).</summary>
    public long Add(in CommitRecord record)
    {
        var id = Intern(record.Sha);
        var ps = record.Parents.Length == 0 ? [] : new int[record.Parents.Length];
        for (var i = 0; i < ps.Length; i++)
            ps[i] = Intern(record.Parents[i]);
        parents[id] = ps;
        rowOf[id] = rows.Count;
        rows.Add(id);
        text.Add(record.Text);
        // A SHA string (40 chars) plus its dictionary and list slots, the parent array, the row structs, then the text.
        return 200 + 4L * ps.Length + 40L * ps.Length + record.Text.Bytes;
    }
}

/// <summary>A row's text: about 90% of a snapshot's memory (D27), so it is what the cache's byte budget mostly counts.</summary>
public sealed record CommitText(string AuthorName, string AuthorEmail, string Date, string Subject)
{
    public long Bytes => 4 * 24 + 2L * (AuthorName.Length + AuthorEmail.Length + Date.Length + Subject.Length);
}

public readonly record struct CommitRecord(string Sha, string[] Parents, CommitText Text);

/// <summary>
/// Streams <c>git log -z --format=%H%x00%P%x00%an%x00%ae%x00%aI%x00%s</c>: six NUL-terminated fields per commit, read
/// lazily so the first rows arrive without walking the whole history (PRD §4.3).
/// </summary>
public sealed class CommitGraphReader(Stream stream)
{
    /// <summary>The <c>--format</c> the reader parses.</summary>
    public const string Format = "--format=%H%x00%P%x00%an%x00%ae%x00%aI%x00%s";

    /// <summary>Subjects are cut here: a row is a one-line list entry, and a hostile repository's 1 MB subject must not
    /// inflate the snapshot.</summary>
    public const int MaxSubjectLength = 500;

    private const int MaxFieldLength = 64 * 1024;

    private readonly byte[] buffer = new byte[64 * 1024];
    private readonly MemoryStream token = new();
    private int length, position;

    /// <summary>The next commit, or null at the end of the output.</summary>
    public async ValueTask<CommitRecord?> ReadAsync(CancellationToken cancellationToken)
    {
        var sha = await NextAsync(cancellationToken);
        if (sha is null)
            return null;
        sha = sha.TrimStart('\n');
        if (sha.Length == 0)
            return null;
        var parents = await NextAsync(cancellationToken) ?? "";
        var authorName = await NextAsync(cancellationToken) ?? "";
        var authorEmail = await NextAsync(cancellationToken) ?? "";
        var date = await NextAsync(cancellationToken) ?? "";
        var subject = await NextAsync(cancellationToken) ?? "";
        if (subject.Length > MaxSubjectLength)
            subject = subject[..MaxSubjectLength];
        return new CommitRecord(sha, parents.Length == 0 ? [] : parents.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            new CommitText(authorName, authorEmail, date, subject));
    }

    private async ValueTask<string?> NextAsync(CancellationToken cancellationToken)
    {
        token.SetLength(0);
        while (true)
        {
            if (position == length)
            {
                length = await stream.ReadAsync(buffer, cancellationToken);
                position = 0;
                if (length == 0)
                    return token.Length == 0 ? null : Decode();
            }
            var nul = Array.IndexOf(buffer, (byte)0, position, length - position);
            if (nul < 0)
            {
                Append(position, length - position);
                position = length;
                continue;
            }
            Append(position, nul - position);
            position = nul + 1;
            return Decode();
        }
    }

    private void Append(int offset, int count)
    {
        var room = MaxFieldLength - (int)token.Length;
        if (room > 0)
            token.Write(buffer, offset, Math.Min(count, room));
    }

    private string Decode() => Encoding.UTF8.GetString(token.GetBuffer(), 0, (int)token.Length);
}
