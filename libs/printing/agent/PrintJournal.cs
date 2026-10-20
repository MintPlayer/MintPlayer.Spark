namespace MintPlayer.Spark.Printing.Agent;

/// <summary>
/// The agent's local job journal (PRD §4.11, WD7, W16): an append-only file, flushed to disk on every record, that says per
/// job id whether sending started, whether the printer took every byte, and whether the app acknowledged it.
/// </summary>
/// <remarks>
/// <para>The rules that make each job id print at most once and be acknowledged exactly once:</para>
/// <list type="bullet">
/// <item>printed → never sent again; a redelivery is acknowledged without printing;</item>
/// <item>started but not printed (the agent stopped while sending, or the connection broke after bytes went out) → the
/// agent cannot know whether a label came out, so it never resends on its own: it reports the job as uncertain and fails
/// it, and a person reprints it as a new job after looking at the printer;</item>
/// <item>printed but not acknowledged → acknowledged again until the app answers.</item>
/// </list>
/// A record is one line, <c>{S|U|P|A|F} {jobId}</c> (started; unstarted: nothing was sent; printed; acknowledged; failed
/// and reported: an uncertain job the app was told about).
/// </remarks>
public sealed class PrintJournal : IDisposable
{
    private const int Keep = 20_000;
    private readonly object gate = new();
    private readonly string path;
    private readonly Dictionary<string, char> states = new(StringComparer.Ordinal);
    private FileStream stream;
    private int records;

    public PrintJournal(string path)
    {
        this.path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (File.Exists(path))
            foreach (var line in File.ReadLines(path))
                if (line.Length > 2 && line[1] == ' ')
                {
                    Apply(line[0], line[2..]);
                    records++;
                }
        stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        if (records > Keep * 2)
            Compact();
    }

    /// <summary>Printed: every byte reached the printer.</summary>
    public bool IsPrinted(string jobId) { lock (gate) return states.TryGetValue(jobId, out var s) && s is 'P' or 'A'; }

    /// <summary>Started and not finished: whether a label came out is unknown.</summary>
    public bool IsUncertain(string jobId) { lock (gate) return states.TryGetValue(jobId, out var s) && s == 'S'; }

    public bool IsAcknowledged(string jobId) { lock (gate) return states.TryGetValue(jobId, out var s) && s == 'A'; }

    /// <summary>Printed jobs the app has not acknowledged yet.</summary>
    public IReadOnlyList<string> PendingAcknowledgements() { lock (gate) return states.Where(s => s.Value == 'P').Select(s => s.Key).ToList(); }

    /// <summary>Jobs whose sending started and never finished.</summary>
    public IReadOnlyList<string> Uncertain() { lock (gate) return states.Where(s => s.Value == 'S').Select(s => s.Key).ToList(); }

    public void Started(string jobId) => Record('S', jobId);

    /// <summary>Sending failed before any byte left: the job may be sent again later.</summary>
    public void Unstarted(string jobId) => Record('U', jobId);

    public void Printed(string jobId) => Record('P', jobId);

    public void Acknowledged(string jobId) => Record('A', jobId);

    /// <summary>An uncertain job the app was told about (failed): it stays uncertain locally, but is no longer reported.</summary>
    public void Resolved(string jobId) => Record('F', jobId);

    private void Record(char state, string jobId)
    {
        if (jobId.Contains('\n') || jobId.Contains('\r'))
            throw new ArgumentException("A job id holds no line break.", nameof(jobId));
        lock (gate)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes($"{state} {jobId}\n");
            stream.Write(bytes);
            // On disk before the next step: the journal is only worth something if it survives a power cut.
            stream.Flush(flushToDisk: true);
            Apply(state, jobId);
            if (++records > Keep * 2)
                Compact();
        }
    }

    private void Apply(char state, string jobId)
    {
        if (state == 'U')
            states.Remove(jobId);
        else
            states[jobId] = state;
    }

    /// <summary>Rewrites the journal with the current state, dropping the oldest acknowledged jobs beyond <see cref="Keep"/>.</summary>
    private void Compact()
    {
        stream.Dispose();
        var acknowledged = states.Where(s => s.Value is 'A' or 'F').Select(s => s.Key).ToList();
        foreach (var old in acknowledged.Take(Math.Max(0, acknowledged.Count - Keep)))
            states.Remove(old);
        var temp = path + ".tmp";
        File.WriteAllLines(temp, states.Select(s => $"{s.Value} {s.Key}"));
        File.Move(temp, path, overwrite: true);
        records = states.Count;
        stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
    }

    public void Dispose()
    {
        lock (gate)
            stream.Dispose();
    }
}
