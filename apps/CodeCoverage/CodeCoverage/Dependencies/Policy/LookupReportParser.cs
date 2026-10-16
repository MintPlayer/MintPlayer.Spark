using CodeCoverage.Git.Dependencies;

namespace CodeCoverage.Dependencies.Policy;

/// <summary>The lookup report as the policy reads it, or why it was refused.</summary>
public sealed record LookupReportParse(LookupDocument? Document, IReadOnlyList<string> Errors)
{
    public bool IsValid => Document is not null && Errors.Count == 0;
}

/// <summary>
/// Reads the lookup run's <c>lookup.json</c> (R1, R3): schema first (<see cref="DependencyContractReader"/>), then the
/// checks the schema cannot make. A refused report plans nothing; it never throws.
/// </summary>
/// <remarks>
/// The records are trusted within R17's guards (the lookup runs only pinned Renovate on the default branch). The engine
/// still holds every record whose paths the plan schema would refuse (gap A #6) and every candidate missing a field (R1),
/// so this parser does not drop records: it only refuses a document that is not for the commit the bot dispatched.
/// </remarks>
public static class LookupReportParser
{
    public static LookupReportParse Parse(string json, string expectedSha)
    {
        var read = DependencyContractReader.ReadLookup(json);
        if (!read.IsValid)
            return new LookupReportParse(null, read.Errors);

        var document = read.Document!;
        var errors = new List<string>();
        if (!string.Equals(document.ExpectedSha, expectedSha, StringComparison.Ordinal))
            errors.Add($"the report is for {document.ExpectedSha}, not the dispatched {expectedSha} (R3)");
        foreach (var platform in document.Platforms.Where(p => p.ContractVersion != DependencyContract.Version))
            errors.Add($"the {platform.Sdk} platform record has contract version {platform.ContractVersion}");
        return errors.Count == 0 ? new LookupReportParse(document, []) : new LookupReportParse(null, errors);
    }
}
