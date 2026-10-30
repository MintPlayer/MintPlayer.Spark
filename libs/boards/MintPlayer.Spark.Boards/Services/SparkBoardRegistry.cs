using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Boards.Services;

/// <summary>
/// The boards the application ships: <c>App_Data/boards.json</c> and <see cref="SparkBoardsOptions.Boards"/> (D11).
/// Read once and validated at startup; a malformed file refuses startup instead of failing every request.
/// </summary>
internal sealed partial class SparkBoardRegistry
{
    public const string FileName = "boards.json";

    [Inject] private readonly IHostEnvironment hostEnvironment;
    [Inject] private readonly IOptions<SparkBoardsOptions> options;
    [Inject] private readonly ITranslationsLoader translationsLoader;

    private IReadOnlyDictionary<string, SparkBoardDefinition>? boards;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public IReadOnlyDictionary<string, SparkBoardDefinition> Boards
        => LazyInitializer.EnsureInitialized(ref boards, Load);

    public SparkBoardDefinition? Find(string? id)
        => id is not null && Boards.TryGetValue(id, out var board) ? board : null;

    /// <summary>A caption declared as a translation key (<c>"title": "boards.home"</c>) resolved against the translations.</summary>
    public TranslatedString? Resolve(TranslatedString? title)
        => title is { Key: { } key, Translations.Count: 0 } ? translationsLoader.Resolve(key) ?? title : title;

    private Dictionary<string, SparkBoardDefinition> Load()
    {
        var all = new Dictionary<string, SparkBoardDefinition>(StringComparer.Ordinal);
        var path = SparkAppData.Path(hostEnvironment.ContentRootPath, FileName);
        if (File.Exists(path))
        {
            BoardsFile? file;
            try
            {
                file = JsonSerializer.Deserialize<BoardsFile>(File.ReadAllText(path), JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"{SparkAppData.Relative(FileName)} is not valid JSON: {ex.Message}", ex);
            }
            foreach (var board in file?.Boards ?? [])
                Add(all, board, SparkAppData.Relative(FileName));
        }
        foreach (var board in options.Value.Boards)
            Add(all, board, "SparkBoardsOptions.Boards");
        return all;
    }

    private static void Add(Dictionary<string, SparkBoardDefinition> all, SparkBoardDefinition board, string source)
    {
        Validate(board, source);
        foreach (var path in board.Paths)
            if (all.Values.FirstOrDefault(other => other.Paths.Contains(path, StringComparer.OrdinalIgnoreCase)) is { } other)
                throw new InvalidOperationException($"{source}: board '{board.Id}' shows on '{path}', where board '{other.Id}' already shows.");
        if (!all.TryAdd(board.Id, board))
            throw new InvalidOperationException($"{source} declares board '{board.Id}', which is already declared.");
    }

    /// <summary>The board a page shows (<see cref="SparkBoardDefinition.Paths"/>), compared as the prerendered pages are: ignoring case.</summary>
    public SparkBoardDefinition? FindByPath(string? path)
        => path is null ? null : Boards.Values.FirstOrDefault(b => b.Paths.Contains(path, StringComparer.OrdinalIgnoreCase));

    /// <summary>The structural rules; that each kind exists is checked at startup with the registered kinds.</summary>
    internal static void Validate(SparkBoardDefinition board, string source)
    {
        if (!SparkBoardIds.IsValid(board.Id))
            throw new InvalidOperationException($"{source}: board id '{board.Id}' must match {SparkBoardIds.Pattern}.");
        foreach (var path in board.Paths)
            if (!path.StartsWith('/') || path.Contains('?') || path.Contains('#'))
                throw new InvalidOperationException($"{source}: board '{board.Id}' path '{path}' must be a page path starting with '/'.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tile in board.Tiles)
        {
            if (!TileIds.IsValid(tile.Id))
                throw new InvalidOperationException($"{source}: board '{board.Id}' has tile id '{tile.Id}', which must match {TileIds.Pattern}.");
            if (tile.Id.StartsWith(TileIds.UserPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException($"{source}: board '{board.Id}' tile '{tile.Id}' starts with '{TileIds.UserPrefix}', which is reserved for the tiles users add.");
            if (!ids.Add(tile.Id))
                throw new InvalidOperationException($"{source}: board '{board.Id}' declares tile '{tile.Id}' twice.");
            if (string.IsNullOrWhiteSpace(tile.Kind))
                throw new InvalidOperationException($"{source}: board '{board.Id}' tile '{tile.Id}' has no kind.");
        }
        foreach (var (count, layout) in board.Layouts)
        {
            if (count is < 1 or > 24)
                throw new InvalidOperationException($"{source}: board '{board.Id}' has a layout for {count} columns; 1 to 24 are allowed.");
            foreach (var entry in layout)
            {
                if (!ids.Contains(entry.Id))
                    throw new InvalidOperationException($"{source}: board '{board.Id}' layout {count} places tile '{entry.Id}', which the board does not declare.");
                if (!BoardMerge.IsValidPosition(entry.Position, count))
                    throw new InvalidOperationException($"{source}: board '{board.Id}' layout {count} places tile '{entry.Id}' outside {count} columns.");
            }
        }
    }

    private sealed class BoardsFile
    {
        [JsonPropertyName("$schema")] public string? Schema { get; set; }
        public List<SparkBoardDefinition> Boards { get; set; } = [];
    }
}

internal static partial class TileIds
{
    public const string Pattern = "^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$";

    /// <summary>The id space of the tiles users add, so they can never collide with a later default.</summary>
    public const string UserPrefix = "u-";

    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant)]
    private static partial Regex IdRegex();

    public static bool IsValid(string? id) => id is not null && IdRegex().IsMatch(id);
}
