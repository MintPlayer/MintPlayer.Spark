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
    /// Roll-up over <see cref="Files"/>.
    /// </summary>
    public string ModelFiles { get; set; } = string.Empty;

    /// <summary>
    /// Structural hash of each file in <c>App_Data/Model</c>, keyed by file name.
    /// <para>
    /// The entity hashes describe what the CLR classes require; these describe what is actually on
    /// disk. Without them a file planted in the model directory would be invisible to verification
    /// and still be loaded, because the loader globs the whole directory.
    /// </para>
    /// <para>
    /// Only structural fields contribute — labels, renderers, groups and ordering are excluded, so
    /// the hand-editing workflow the model supports does not trip the check.
    /// </para>
    /// </summary>
    public SortedDictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);

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
    /// Structural fingerprint of every file in the model directory, keyed by file name.
    /// See <see cref="ModelFileShape"/> for what counts as structural — presentational fields such
    /// as labels are excluded so hand-editing them does not stop an application from starting.
    /// </summary>
    public static SortedDictionary<string, string> ComputeFileHashes(string contentRootPath)
        => ModelFileShape.ComputeFileHashes(ModelDirectoryFor(contentRootPath));

    /// <summary>Roll-up over the per-file structural hashes.</summary>
    public static string CombineFileHashes(IReadOnlyDictionary<string, string> fileHashes)
    {
        var builder = new StringBuilder();
        foreach (var entry in fileHashes.OrderBy(e => e.Key, StringComparer.Ordinal))
            builder.Append(entry.Key).Append(':').Append(entry.Value).Append('\n');
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
