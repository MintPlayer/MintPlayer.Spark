using System.Reflection;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Encryption.Abstractions;
using MintPlayer.Spark.Encryption.Abstractions.Configuration;
using MintPlayer.Spark.Encryption.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Encryption;

public static class SparkEncryptionExtensions
{
    /// <summary>
    /// Registers field-level encryption services and configuration.
    /// </summary>
    public static IServiceCollection AddSparkEncryption(
        this IServiceCollection services,
        Action<SparkEncryptionOptions> configure)
    {
        services.Configure(configure);
        services.AddSingleton<FieldEncryptionService>();
        services.AddSingleton<EncryptedFieldResolver>();
        return services;
    }

    /// <summary>
    /// Hooks field-level encryption into the RavenDB <see cref="IDocumentStore"/> events,
    /// validates attribute usage, and auto-generates a dev key if needed.
    /// </summary>
    public static WebApplication UseSparkEncryption(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SparkEncryption");
        var options = app.Services.GetRequiredService<IOptions<SparkEncryptionOptions>>().Value;
        var encryptionService = app.Services.GetRequiredService<FieldEncryptionService>();
        var fieldResolver = app.Services.GetRequiredService<EncryptedFieldResolver>();
        var store = app.Services.GetRequiredService<IDocumentStore>();

        // Startup validation: scan for [Encrypted] on non-string properties
        ValidateEncryptedAttributes(logger);

        // Auto-generate dev key if not configured
        var hostEnvironment = app.Services.GetRequiredService<IHostEnvironment>();
        if (hostEnvironment.IsDevelopment() && string.IsNullOrEmpty(options.OwnKey))
        {
            var key = new byte[32];
            RandomNumberGenerator.Fill(key);
            options.OwnKey = Convert.ToBase64String(key);
            logger.LogWarning(
                "SparkEncryption: No OwnKey configured — auto-generated a development key. " +
                "Set SparkEncryption:OwnKey in appsettings to use a stable key across restarts.");
        }

        // Hook into BeforeStore to encrypt
        store.OnBeforeStore += (sender, args) =>
        {
            var entityType = args.Entity.GetType();
            var properties = fieldResolver.GetEncryptedProperties(entityType);
            if (properties.Length == 0) return;

            var key = fieldResolver.GetKeyForEntity(entityType);
            if (key == null)
            {
                logger.LogWarning("No encryption key available for {EntityType} — skipping encryption", entityType.Name);
                return;
            }

            foreach (var prop in properties)
            {
                var value = (string?)prop.GetValue(args.Entity);
                if (value != null && !FieldEncryptionService.IsEncrypted(value))
                {
                    var encrypted = encryptionService.Encrypt(value, key);
                    prop.SetValue(args.Entity, encrypted);
                }
            }
        };

        // Hook into AfterConversionToEntity to decrypt
        store.OnAfterConversionToEntity += (sender, args) =>
        {
            var entityType = args.Entity.GetType();
            var properties = fieldResolver.GetEncryptedProperties(entityType);
            if (properties.Length == 0) return;

            var key = fieldResolver.GetKeyForEntity(entityType);
            if (key == null)
            {
                logger.LogWarning("No decryption key available for {EntityType} — encrypted fields will remain as ciphertext", entityType.Name);
                return;
            }

            foreach (var prop in properties)
            {
                var value = (string?)prop.GetValue(args.Entity);
                if (FieldEncryptionService.IsEncrypted(value))
                {
                    var decrypted = encryptionService.Decrypt(value!, key);
                    prop.SetValue(args.Entity, decrypted);
                }
            }
        };

        logger.LogInformation("SparkEncryption: Field-level encryption hooks registered on IDocumentStore");
        return app;
    }

    private static void ValidateEncryptedAttributes(ILogger logger)
    {
        var entryAssembly = Assembly.GetEntryAssembly();
        if (entryAssembly == null) return;

        var referencedAssemblies = entryAssembly.GetReferencedAssemblies()
            .Select(name =>
            {
                try { return Assembly.Load(name); }
                catch { return null; }
            })
            .Where(a => a != null)
            .Cast<Assembly>()
            .Append(entryAssembly);

        var violations = new List<string>();

        foreach (var assembly in referencedAssemblies)
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).Cast<Type>().ToArray(); }

            foreach (var type in types)
            {
                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (prop.GetCustomAttribute<EncryptedAttribute>() != null && prop.PropertyType != typeof(string))
                    {
                        violations.Add($"{type.FullName}.{prop.Name} ({prop.PropertyType.Name})");
                    }
                }
            }
        }

        if (violations.Count > 0)
        {
            throw new InvalidOperationException(
                $"[Encrypted] can only be applied to string properties. Invalid usage found on: {string.Join(", ", violations)}");
        }
    }
}
