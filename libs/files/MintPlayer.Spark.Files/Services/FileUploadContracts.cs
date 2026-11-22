using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Files.Services;

/// <summary>One file a prepare asks a slot for: its name and the size the client says it has.</summary>
public sealed record PrepareFileInput(string? FileName, long Length);

/// <summary><c>POST /spark/files/prepare</c>: slots for <see cref="Files"/> on <see cref="Attribute"/> of a new (<see cref="Id"/> null) or existing object.</summary>
public sealed record PrepareFilesInput(Guid ObjectTypeId, string? Attribute, string? Id, IReadOnlyList<PrepareFileInput>? Files);

/// <summary>
/// A slot; then commit <see cref="Token"/>. <see cref="Method"/> <c>POST</c>: a multipart form of the <see cref="Fields"/>, in
/// order, then the file as <c>file</c>, to <see cref="Url"/>. <c>PUT</c>: the raw bytes to <see cref="Url"/> with the
/// <see cref="Headers"/>. <see cref="Direct"/>: the bytes go straight to the store, not through Spark (no antiforgery header,
/// no cookies).
/// </summary>
public sealed record PreparedFile(string Token, string FileName, string Url, string Method, IReadOnlyDictionary<string, string> Fields, IReadOnlyDictionary<string, string> Headers, bool Direct);

public sealed record PrepareFilesOutput(IReadOnlyList<PreparedFile> Files);

/// <summary><c>POST /spark/files/commit</c>: check the uploaded bytes of each token.</summary>
public sealed record CommitFilesInput(IReadOnlyList<string>? Tokens);

/// <summary>A committed file. Claimable by a save when <see cref="Error"/> is null and <see cref="ScanStatus"/> is <c>Clean</c>.</summary>
public sealed record CommittedFile(
    string Token,
    string? FileName,
    string? ContentType,
    long Length,
    SparkFileScanStatus ScanStatus,
    int? Width,
    int? Height,
    string? Error);

public sealed record CommitFilesOutput(IReadOnlyList<CommittedFile> Files);
