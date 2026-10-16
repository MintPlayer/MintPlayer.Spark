using System.Text;
using System.Text.Json;
using CodeCoverage.Git.Dependencies;
using Json.Schema;

namespace CodeCoverage.Dependencies;

/// <summary>The five schemas of the dependency-update contract (<c>apps/CodeCoverage/contracts/dependency-update</c>).</summary>
public enum EDependencyContractSchema
{
    /// <summary><c>lookup.json</c>, record.schema.json.</summary>
    Lookup,

    /// <summary><c>.mintplayer/dependency-plan.json</c>, plan.schema.json.</summary>
    Plan,

    /// <summary><c>result.json</c>, result.schema.json.</summary>
    ApplyResult,

    /// <summary>One platform record, platform-record.schema.json.</summary>
    Platform,

    /// <summary><c>migrate-result.json</c>, migrate-result.schema.json.</summary>
    MigrateResult,
}

/// <summary>A contract document read from a runner: the document, or why it was refused. Errors are data, never thrown.</summary>
public sealed record DependencyContractRead<T>(T? Document, IReadOnlyList<string> Errors) where T : class
{
    public bool IsValid => Document is not null && Errors.Count == 0;
}

/// <summary>
/// Reads and writes the dependency-update contract documents, schema first (PRD §13.11.2 "Contract test").
/// </summary>
/// <remarks>
/// <para>
/// Every document a runner returns came from a job the bot does not trust (the apply runner installs fresh packages; the
/// lookup runner is trusted only within R17's guards). So it is validated against the same schema the runner validated it
/// with (ajv) before a single field is read: a document the schema refuses never reaches the policy engine. The schemas are
/// embedded from <c>contracts/dependency-update</c>, the one source both sides read.
/// </para>
/// <para>
/// The plan the bot writes is validated too, and an invalid plan throws: that is a bug in the bot, and committing it would
/// hand the runner something its own validation refuses.
/// </para>
/// </remarks>
public static class DependencyContractReader
{
    /// <summary>The artifact read cap the merge queue uses for its patch (8 MB); a contract document is far smaller.</summary>
    public const int MaxBytes = 8 * 1024 * 1024;

    private static readonly Lazy<IReadOnlyDictionary<EDependencyContractSchema, JsonSchema>> Schemas = new(Build);

    /// <summary>The schema's own text, as embedded.</summary>
    public static string SchemaText(EDependencyContractSchema schema) => ReadResource(FileName(schema));

    public static string FileName(EDependencyContractSchema schema) => schema switch
    {
        EDependencyContractSchema.Lookup => "record.schema.json",
        EDependencyContractSchema.Plan => "plan.schema.json",
        EDependencyContractSchema.ApplyResult => "result.schema.json",
        EDependencyContractSchema.Platform => "platform-record.schema.json",
        EDependencyContractSchema.MigrateResult => "migrate-result.schema.json",
        _ => throw new ArgumentOutOfRangeException(nameof(schema)),
    };

    /// <summary>Validates <paramref name="json"/> against <paramref name="schema"/>; empty when valid.</summary>
    public static IReadOnlyList<string> Validate(EDependencyContractSchema schema, string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxBytes)
            return [$"the document is larger than {MaxBytes} bytes"];

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return [$"not JSON: {ex.Message}"];
        }

        using (document)
        {
            var results = Schemas.Value[schema].Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
            if (results.IsValid)
                return [];

            return [.. (results.Details ?? [])
                .Where(d => d.Errors is { Count: > 0 })
                .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Key}: {e.Value}"))
                .Distinct(StringComparer.Ordinal)
                .DefaultIfEmpty($"the document does not match {FileName(schema)}")];
        }
    }

    public static DependencyContractRead<LookupDocument> ReadLookup(string json) => Read<LookupDocument>(EDependencyContractSchema.Lookup, json);

    public static DependencyContractRead<DependencyPlan> ReadPlan(string json) => Read<DependencyPlan>(EDependencyContractSchema.Plan, json);

    public static DependencyContractRead<ApplyResult> ReadApplyResult(string json) => Read<ApplyResult>(EDependencyContractSchema.ApplyResult, json);

    public static DependencyContractRead<PlatformRecord> ReadPlatform(string json) => Read<PlatformRecord>(EDependencyContractSchema.Platform, json);

    public static DependencyContractRead<MigrateResult> ReadMigrateResult(string json) => Read<MigrateResult>(EDependencyContractSchema.MigrateResult, json);

    /// <summary>Schema first, then the DTO. Either failing refuses the document.</summary>
    public static DependencyContractRead<T> Read<T>(EDependencyContractSchema schema, string json) where T : class
    {
        var errors = Validate(schema, json);
        if (errors.Count > 0)
            return new DependencyContractRead<T>(null, errors);

        try
        {
            var document = DependencyContract.Deserialize<T>(json);
            return document is null
                ? new DependencyContractRead<T>(null, ["the document is null"])
                : new DependencyContractRead<T>(document, []);
        }
        catch (JsonException ex)
        {
            return new DependencyContractRead<T>(null, [$"the document does not map onto the contract: {ex.Message}"]);
        }
    }

    /// <summary>Serializes the bot's own plan and validates it against plan.schema.json.</summary>
    /// <exception cref="InvalidOperationException">The plan does not validate: a bug in the bot, never committed.</exception>
    public static string WritePlan(DependencyPlan plan)
    {
        var json = DependencyContract.Serialize(plan);
        var errors = Validate(EDependencyContractSchema.Plan, json);
        if (errors.Count > 0)
            throw new InvalidOperationException($"The dependency plan '{plan.PlanId}' does not validate against plan.schema.json: {string.Join("; ", errors)}");
        return json;
    }

    private static IReadOnlyDictionary<EDependencyContractSchema, JsonSchema> Build()
    {
        // One registry for the five, so record.schema.json's $ref to platform-record.schema.json resolves by its $id. The
        // referenced schema is built first: references are resolved during the build.
        var options = new BuildOptions { SchemaRegistry = new SchemaRegistry() };
        var order = new[]
        {
            EDependencyContractSchema.Platform,
            EDependencyContractSchema.Lookup,
            EDependencyContractSchema.Plan,
            EDependencyContractSchema.ApplyResult,
            EDependencyContractSchema.MigrateResult,
        };
        var built = new Dictionary<EDependencyContractSchema, JsonSchema>();
        foreach (var schema in order)
            built[schema] = JsonSchema.FromText(SchemaText(schema), options);
        return built;
    }

    private static string ReadResource(string fileName)
    {
        var name = $"DependencyContracts.{fileName}";
        using var stream = typeof(DependencyContractReader).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The embedded contract schema '{name}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
