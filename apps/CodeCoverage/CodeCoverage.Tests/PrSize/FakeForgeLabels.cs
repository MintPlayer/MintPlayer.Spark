using CodeCoverage.Entities;
using CodeCoverage.Forge;

namespace CodeCoverage.Tests.PrSize;

/// <summary>An in-memory forge with GitHub's label semantics (S1): names unique case-insensitively, a rename keeps the label on its pull requests, a delete takes it off them.</summary>
internal sealed class FakeForgeLabels(EForgeProvider provider = EForgeProvider.GitHub) : IForgeLabels
{
    public EForgeProvider Provider => provider;

    public List<ForgeLabel> Labels { get; } = [];
    public Dictionary<int, List<string>> PullRequests { get; } = [];
    public List<ForgePullRequestFile> Files { get; } = [];
    public List<(long Id, string Sha, ForgeSizeCheck Check)> Checks { get; } = [];

    /// <summary>Every call that writes, in order, e.g. <c>create size/M</c>, <c>add #7 size/M</c>.</summary>
    public List<string> Writes { get; } = [];
    public int FileListings { get; private set; }

    /// <summary>Thrown by the next write whose description starts with the key.</summary>
    public Dictionary<string, Exception> FailOn { get; } = [];

    public Task<IReadOnlyList<ForgeLabel>> ListRepositoryLabelsAsync(Repository repository, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ForgeLabel>>(Labels.ToArray());

    public Task<ForgeLabelWrite> CreateLabelAsync(Repository repository, ForgeLabelDefinition label, CancellationToken cancellationToken = default)
    {
        Write($"create {label.Name}");
        if (Find(label.Name) is not null) return Task.FromResult(ForgeLabelWrite.Conflict);
        Labels.Add(new ForgeLabel(label.Name, label.Color, label.Description, false));
        return Task.FromResult(ForgeLabelWrite.Done);
    }

    public Task<ForgeLabelWrite> UpdateLabelAsync(Repository repository, string currentName, ForgeLabelDefinition label, CancellationToken cancellationToken = default)
    {
        Write($"update {currentName} -> {label.Name}");
        if (Find(currentName) is not { } current) return Task.FromResult(ForgeLabelWrite.NotFound);
        if (Find(label.Name) is { } clash && clash != current) return Task.FromResult(ForgeLabelWrite.Conflict);
        Labels[Labels.IndexOf(current)] = current with { Name = label.Name, Color = label.Color, Description = label.Description };
        foreach (var carried in PullRequests.Values)
            for (var i = 0; i < carried.Count; i++)
                if (string.Equals(carried[i], currentName, StringComparison.OrdinalIgnoreCase)) carried[i] = label.Name;
        return Task.FromResult(ForgeLabelWrite.Done);
    }

    public Task<ForgeLabelWrite> DeleteLabelAsync(Repository repository, string name, CancellationToken cancellationToken = default)
    {
        Write($"delete {name}");
        if (Find(name) is not { } current) return Task.FromResult(ForgeLabelWrite.NotFound);
        Labels.Remove(current);
        foreach (var carried in PullRequests.Values)
            carried.RemoveAll(l => string.Equals(l, name, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(ForgeLabelWrite.Done);
    }

    public Task<IReadOnlyList<string>> ListPullRequestLabelsAsync(Repository repository, int number, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(Carried(number).ToArray());

    public Task AddLabelAsync(Repository repository, int number, string label, CancellationToken cancellationToken = default)
    {
        Write($"add #{number} {label}");
        Carried(number).Add(label);
        return Task.CompletedTask;
    }

    public Task<bool> RemoveLabelAsync(Repository repository, int number, string label, CancellationToken cancellationToken = default)
    {
        Write($"remove #{number} {label}");
        return Task.FromResult(Carried(number).RemoveAll(l => string.Equals(l, label, StringComparison.OrdinalIgnoreCase)) > 0);
    }

    public Task<IReadOnlyList<ForgePullRequestFile>> ListPullRequestFilesAsync(Repository repository, int number, CancellationToken cancellationToken = default)
    {
        FileListings++;
        if (FailOn.Remove("files", out var ex)) throw ex;
        return Task.FromResult<IReadOnlyList<ForgePullRequestFile>>(Files.ToArray());
    }

    public Task<long> PublishSizeCheckAsync(Repository repository, string sha, ForgeSizeCheck check, long? existingId, CancellationToken cancellationToken = default)
    {
        // Idempotent by name and sha, like GitHub's lookup.
        var id = existingId ?? Checks.Where(c => c.Sha == sha).Select(c => (long?)c.Id).FirstOrDefault() ?? Checks.Count + 1;
        Checks.RemoveAll(c => c.Id == id);
        Checks.Add((id, sha, check));
        return Task.FromResult(id);
    }

    public ForgeLabel? Find(string name) => Labels.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));

    private List<string> Carried(int number)
        => PullRequests.TryGetValue(number, out var carried) ? carried : PullRequests[number] = [];

    private void Write(string description)
    {
        var key = FailOn.Keys.FirstOrDefault(description.StartsWith);
        if (key is not null && FailOn.Remove(key, out var ex)) throw ex;
        Writes.Add(description);
    }
}
