using CodeCoverage.Entities;
using CodeCoverage.Forge;

namespace CodeCoverage.PrSize;

/// <summary>
/// The pause between two label-definition writes (PRD D4.2): GitHub asks for at most one content-creating request a
/// second, and an account-wide reconcile writes several labels in a row.
/// </summary>
public class PrSizeLabelPacing
{
    /// <summary>No pause, for tests.</summary>
    public static PrSizeLabelPacing None { get; } = new(TimeSpan.Zero);

    private readonly TimeSpan pace;

    public PrSizeLabelPacing() : this(TimeSpan.FromSeconds(1)) { }

    public PrSizeLabelPacing(TimeSpan pace) => this.pace = pace;

    public Task WaitAsync(CancellationToken cancellationToken)
        => pace == TimeSpan.Zero ? Task.CompletedTask : Task.Delay(pace, cancellationToken);
}

/// <summary>
/// Brings a repository's size labels in line with its account's scale (docs/coverage_pr_size_labels_PRD.md D3), and
/// removes them on request (D4.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Rename, never recreate.</b> A row whose name changed PATCHes the label it wrote before, found by the name recorded in
/// <see cref="Repository.PrSizeLabelState"/>, so every pull request carrying it keeps it (S1). A label the user made with
/// the row's name is adopted instead. Labels the app never wrote, <c>size/foo</c> included, are never touched.
/// </para>
/// <para>
/// <b>State follows the forge.</b> An entry changes only after the write it records succeeded, so a failure halfway leaves
/// the state true to what exists, and the retry carries on from there.
/// </para>
/// </remarks>
public sealed class PrSizeLabelReconciler(IForgeLabels forge, PrSizeLabelPacing pacing)
{
    /// <summary>Whether <paramref name="repository"/>'s recorded labels already match <paramref name="scale"/>.</summary>
    public static bool IsCurrent(Repository repository, PrSizeScale scale)
    {
        if (repository.PrSizeLabelState.Count != scale.Rows.Count) return false;
        foreach (var row in scale.Rows)
        {
            var desired = scale.Desired(row);
            var recorded = repository.PrSizeLabelState.FirstOrDefault(s => s.RowId == row.Id);
            if (recorded is null || recorded.Name != desired.Name || recorded.Color != desired.Color || recorded.Description != desired.Description)
                return false;
        }
        return true;
    }

    /// <summary>Writes what differs and returns the names of the scale's labels the user archived (S1).</summary>
    public async Task<IReadOnlySet<string>> ReconcileAsync(Repository repository, PrSizeScale scale, CancellationToken cancellationToken)
    {
        var state = repository.PrSizeLabelState;
        var existing = (await forge.ListRepositoryLabelsAsync(repository, cancellationToken))
            .GroupBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var archived = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in scale.Rows)
        {
            var desired = scale.Desired(row);
            var definition = new ForgeLabelDefinition(desired.Name, desired.Color, desired.Description);
            var recorded = state.FirstOrDefault(s => s.RowId == row.Id);

            if (existing.TryGetValue(desired.Name, out var current))
            {
                // A label by this name exists: adopt it, restyling it when it differs.
                if (current.Archived)
                    archived.Add(current.Name);
                if (current.Name != desired.Name || current.Color != desired.Color || (current.Description ?? string.Empty) != desired.Description)
                {
                    await pacing.WaitAsync(cancellationToken);
                    await forge.UpdateLabelAsync(repository, current.Name, definition, cancellationToken);
                }
                // The row's previous label, if it had another name, is now a stray of the app's own.
                if (recorded is not null && !string.Equals(recorded.Name, desired.Name, StringComparison.OrdinalIgnoreCase)
                    && existing.ContainsKey(recorded.Name)
                    && !scale.Rows.Any(r => string.Equals(scale.Desired(r).Name, recorded.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    await pacing.WaitAsync(cancellationToken);
                    await forge.DeleteLabelAsync(repository, recorded.Name, cancellationToken);
                    existing.Remove(recorded.Name);
                }
            }
            else if (recorded is not null && existing.TryGetValue(recorded.Name, out var previous))
            {
                // Renamed: PATCH the label the app wrote, so the pull requests carrying it keep it.
                await pacing.WaitAsync(cancellationToken);
                if (await forge.UpdateLabelAsync(repository, recorded.Name, definition, cancellationToken) == ForgeLabelWrite.NotFound)
                    await CreateAsync(repository, definition, cancellationToken);
                else if (previous.Archived)
                    archived.Add(desired.Name);
                existing.Remove(recorded.Name);
            }
            else
            {
                await pacing.WaitAsync(cancellationToken);
                await CreateAsync(repository, definition, cancellationToken);
            }

            existing[desired.Name] = new ForgeLabel(desired.Name, desired.Color, desired.Description, archived.Contains(desired.Name));
            Record(state, desired);
        }

        // Rows the scale no longer has: their labels go, which takes them off every pull request (S1).
        foreach (var stale in state.Where(s => scale.Rows.All(r => r.Id != s.RowId)).ToArray())
        {
            if (!scale.Rows.Any(r => string.Equals(scale.Desired(r).Name, stale.Name, StringComparison.OrdinalIgnoreCase)))
            {
                await pacing.WaitAsync(cancellationToken);
                await forge.DeleteLabelAsync(repository, stale.Name, cancellationToken);
            }
            state.Remove(stale);
        }
        return archived;
    }

    /// <summary>Deletes every label the app recorded, and forgets them.</summary>
    public async Task RemoveAsync(Repository repository, CancellationToken cancellationToken)
    {
        foreach (var label in repository.PrSizeLabelState.ToArray())
        {
            await pacing.WaitAsync(cancellationToken);
            await forge.DeleteLabelAsync(repository, label.Name, cancellationToken);
            repository.PrSizeLabelState.Remove(label);
        }
    }

    private async Task CreateAsync(Repository repository, ForgeLabelDefinition definition, CancellationToken cancellationToken)
    {
        // Created meanwhile, by a user or a redelivery: adopt it.
        if (await forge.CreateLabelAsync(repository, definition, cancellationToken) == ForgeLabelWrite.Conflict)
            await forge.UpdateLabelAsync(repository, definition.Name, definition, cancellationToken);
    }

    private static void Record(List<ManagedLabel> state, ManagedLabel desired)
    {
        state.RemoveAll(s => s.RowId == desired.RowId);
        state.Add(desired);
    }
}
