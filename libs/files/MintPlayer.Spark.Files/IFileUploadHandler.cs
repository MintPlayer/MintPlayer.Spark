using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Files;

/// <summary>
/// The app's say over uploads (#700 §6). Register any number with <c>files.AddUploadHandler&lt;T&gt;()</c>; they run in
/// registration order, and the first refusal wins.
/// </summary>
public interface IFileUploadHandler
{
    /// <summary>
    /// At commit, after the size check and the sniff, before the scan: return <see cref="FileUploadDecision.Refuse"/> to
    /// reject the file with a message for the uploader.
    /// </summary>
    ValueTask<FileUploadDecision> ValidateAsync(FileUploadContext context, CancellationToken cancellationToken)
        => ValueTask.FromResult(FileUploadDecision.Accept);

    /// <summary>After a save claimed a clean file onto an entity.</summary>
    ValueTask OnFileAcceptedAsync(FileAcceptedContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}

public sealed record FileUploadDecision(string? RefusalMessage)
{
    public static FileUploadDecision Accept { get; } = new((string?)null);

    public static FileUploadDecision Refuse(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new FileUploadDecision(message);
    }

    public bool IsRefused => RefusalMessage is not null;
}

/// <summary>A committed file before it becomes claimable. <see cref="OpenAsync"/> reads its bytes again.</summary>
public sealed record FileUploadContext(
    EntityTypeDefinition EntityType,
    EntityAttributeDefinition Attribute,
    string? ObjectId,
    string UserId,
    string FileName,
    string ContentType,
    long Length,
    string Sha256,
    Func<CancellationToken, Task<Stream>> OpenAsync);

/// <summary>A file a save has just claimed onto <see cref="Entity"/>.</summary>
public sealed record FileAcceptedContext(object Entity, EntityAttributeDefinition Attribute, SparkFile File, string UserId);
