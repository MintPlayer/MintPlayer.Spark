using MintPlayer.Spark.Storage;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace MintPlayer.Spark.FileSystem;

/// <summary>
/// File-based implementation of <see cref="ISparkSession"/>.
/// Entities are stored as JSON files in a directory structure: {basePath}/{CollectionName}/{id}.json
/// Supports in-memory unit-of-work pattern with deferred persistence via SaveChangesAsync.
/// </summary>
public class FileSystemSparkSession : ISparkSession
{
    private readonly string _basePath;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly List<(object Entity, Type Type)> _pendingStores = [];
    private readonly List<(string Id, Type? Type)> _pendingDeletes = [];
    private readonly ConcurrentDictionary<string, object> _loadedEntities = new();

    public FileSystemSparkSession(string basePath, JsonSerializerOptions? jsonOptions = null)
    {
        _basePath = basePath;
        _jsonOptions = jsonOptions ?? new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
    }

    public IQueryable<T> Query<T>() where T : class
    {
        return LoadAll<T>().AsQueryable();
    }

    public IQueryable<T> Query<T>(string? indexName) where T : class
    {
        // FileSystem provider ignores index names — just returns all entities
        return Query<T>();
    }

    public Task<T?> LoadAsync<T>(string id) where T : class
    {
        if (_loadedEntities.TryGetValue(id, out var cached) && cached is T typedCached)
            return Task.FromResult<T?>(typedCached);

        var collectionName = GetCollectionName(typeof(T));
        var filePath = Path.Combine(_basePath, collectionName, $"{SanitizeId(id)}.json");

        if (!File.Exists(filePath))
            return Task.FromResult<T?>(null);

        var json = File.ReadAllText(filePath);
        var entity = JsonSerializer.Deserialize<T>(json, _jsonOptions);

        if (entity != null)
        {
            _loadedEntities[id] = entity;
        }

        return Task.FromResult(entity);
    }

    public Task StoreAsync<T>(T entity) where T : class
    {
        _pendingStores.Add((entity, typeof(T)));

        // Ensure entity has an Id
        var idProp = typeof(T).GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
        if (idProp != null)
        {
            var currentId = idProp.GetValue(entity) as string;
            if (string.IsNullOrEmpty(currentId))
            {
                var collectionName = GetCollectionName(typeof(T));
                var newId = $"{collectionName}/{Guid.NewGuid()}";
                idProp.SetValue(entity, newId);
            }
        }

        return Task.CompletedTask;
    }

    public Task SaveChangesAsync()
    {
        // Persist all pending stores
        foreach (var (entity, type) in _pendingStores)
        {
            var idProp = type.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
            var id = idProp?.GetValue(entity) as string;
            if (string.IsNullOrEmpty(id)) continue;

            var collectionName = GetCollectionName(type);
            var dirPath = Path.Combine(_basePath, collectionName);
            Directory.CreateDirectory(dirPath);

            var filePath = Path.Combine(dirPath, $"{SanitizeId(id)}.json");
            var json = JsonSerializer.Serialize(entity, type, _jsonOptions);
            File.WriteAllText(filePath, json);
        }
        _pendingStores.Clear();

        // Process all pending deletes
        foreach (var (id, type) in _pendingDeletes)
        {
            if (type != null)
            {
                var collectionName = GetCollectionName(type);
                var filePath = Path.Combine(_basePath, collectionName, $"{SanitizeId(id)}.json");
                if (File.Exists(filePath))
                    File.Delete(filePath);
            }
            else
            {
                // Try to find and delete by id across all collections
                DeleteById(id);
            }
        }
        _pendingDeletes.Clear();

        return Task.CompletedTask;
    }

    public void Delete(string id)
    {
        _pendingDeletes.Add((id, null));
    }

    public void Delete<T>(T entity) where T : class
    {
        var idProp = typeof(T).GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
        var id = idProp?.GetValue(entity) as string;
        if (!string.IsNullOrEmpty(id))
        {
            _pendingDeletes.Add((id, typeof(T)));
        }
    }

    public void Dispose()
    {
        _pendingStores.Clear();
        _pendingDeletes.Clear();
        _loadedEntities.Clear();
    }

    private List<T> LoadAll<T>() where T : class
    {
        var collectionName = GetCollectionName(typeof(T));
        var dirPath = Path.Combine(_basePath, collectionName);

        if (!Directory.Exists(dirPath))
            return [];

        var results = new List<T>();
        foreach (var filePath in Directory.GetFiles(dirPath, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(filePath);
                var entity = JsonSerializer.Deserialize<T>(json, _jsonOptions);
                if (entity != null)
                {
                    results.Add(entity);
                }
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"Warning: Failed to deserialize {filePath}: {ex.Message}");
            }
        }

        return results;
    }

    private void DeleteById(string id)
    {
        // Id format is typically "CollectionName/guid"
        var slashIndex = id.IndexOf('/');
        if (slashIndex > 0)
        {
            var collectionName = id[..slashIndex];
            var filePath = Path.Combine(_basePath, collectionName, $"{SanitizeId(id)}.json");
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }

    private static string GetCollectionName(Type type)
    {
        return type.Name + "s";
    }

    private static string SanitizeId(string id)
    {
        // Replace slashes with dashes for filesystem-safe filenames
        return id.Replace("/", "--").Replace("\\", "--");
    }
}
