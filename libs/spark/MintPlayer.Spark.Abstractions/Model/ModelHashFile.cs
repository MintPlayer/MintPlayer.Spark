using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Abstractions.Model;

/// <summary>
/// Contents of <c>App_Data/model-hashes.json</c> — the fingerprint of the entity classes the model
/// files were generated from.
///
/// <para>
/// It lives beside <c>App_Data/Model/</c> rather than inside it, because the model loader and the
/// generator both enumerate <c>Model/*.json</c> and deserialize every hit as an entity file; a hash
/// file in there would log a load error on every startup.
/// </para>
///
/// <para>
/// It must travel with the model files as a <em>file</em>, never be baked into the assembly as a
/// generated constant. A constant would always agree with the binary that carries it, so the case
/// this is best at catching — new binaries deployed beside a stale <c>App_Data</c> — would become
/// invisible.
/// </para>
/// </summary>
public sealed class ModelHashFile
{
    /// <summary>File name, relative to the content root's <c>App_Data</c> directory.</summary>
    public const string FileName = "model-hashes.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Format version, so a future change to the canonical text can be detected rather than misread.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Roll-up over <see cref="Entities"/> and <see cref="ContextRoots"/>.</summary>
    public string ModelHash { get; set; } = string.Empty;

    /// <summary>Hash of the set of queryable roots; catches a root being removed.</summary>
    public string ContextRoots { get; set; } = string.Empty;

    /// <summary>
    /// Hash over every file in <c>App_Data/Model</c> — names and contents.
    /// <para>
    /// The shape hashes describe what the entity classes say the model should be; this describes
    /// what is actually on disk. Without it a file planted in the model directory would be invisible
    /// to verification and still be loaded, because the loader globs the whole directory. Any added,
    /// removed or altered model file changes this value and the application refuses to start.
    /// </para>
    /// </summary>
    public string ModelFiles { get; set; } = string.Empty;

    /// <summary>
    /// Per-entity hashes, keyed by entity name. Sharded so a drift message can name the entity that
    /// moved, and so two pull requests touching different entities do not collide on one line.
    /// </summary>
    public SortedDictionary<string, string> Entities { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Absolute path of the hash file for a given content root.</summary>
    public static string PathFor(string contentRootPath)
        => Path.Combine(contentRootPath, "App_Data", FileName);

    /// <summary>Absolute path of the model directory for a given content root.</summary>
    public static string ModelDirectoryFor(string contentRootPath)
        => Path.Combine(contentRootPath, "App_Data", "Model");

    /// <summary>
    /// Fingerprints the model directory: every file's name paired with a hash of its contents,
    /// ordinally sorted.
    /// <para>
    /// Line endings are normalised before hashing. The file is written on a developer's machine and
    /// verified inside a Linux container, and git's autocrlf handling rewrites them in between —
    /// without this, every containerised deployment would fail verification.
    /// </para>
    /// <para>
    /// The hash file itself is excluded, since it cannot contain its own hash.
    /// </para>
    /// </summary>
    public static string ComputeModelFilesHash(string contentRootPath)
    {
        var modelDirectory = ModelDirectoryFor(contentRootPath);
        if (!Directory.Exists(modelDirectory))
            return Sha256Hex(string.Empty);

        var builder = new StringBuilder();

        var files = Directory.GetFiles(modelDirectory, "*.json")
            .Where(f => !string.Equals(Path.GetFileName(f), FileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal);

        foreach (var file in files)
        {
            var normalized = File.ReadAllText(file).Replace("\r\n", "\n").Replace("\r", "\n");
            builder.Append(Path.GetFileName(file)).Append(':').Append(Sha256Hex(normalized)).Append('\n');
        }

        return Sha256Hex(builder.ToString());
    }

    private static string Sha256Hex(string text)
        => Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(new UTF8Encoding(false).GetBytes(text)));

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    /// <summary>
    /// Reads the hash file, or <see langword="null"/> when it is absent or unreadable. Callers decide
    /// what absence means — the startup check treats it as a failure rather than a reason to skip,
    /// so the gate cannot be bypassed by deleting one file.
    /// </summary>
    public static ModelHashFile? Read(string contentRootPath)
    {
        var path = PathFor(contentRootPath);
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ModelHashFile>(File.ReadAllText(path), SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Write(string contentRootPath)
    {
        var path = PathFor(contentRootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, ToJson());
    }
}
