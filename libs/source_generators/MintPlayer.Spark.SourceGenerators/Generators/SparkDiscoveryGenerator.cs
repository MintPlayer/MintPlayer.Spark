using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MintPlayer.Spark.SourceGenerators.Models;
using MintPlayer.SourceGenerators.Tools;

namespace MintPlayer.Spark.SourceGenerators.Generators;

/// <summary>
/// The one discovery generator (X20 item 3): a single pass over the project's classes emits
/// <c>AddSparkDiscovered(this ISparkBuilder)</c>, which registers every Actions class, custom action,
/// message recipient, cron job, migration (the app's own and those referenced packages ship),
/// persistence interceptor, subscription worker and row policy, plus every implementation of an
/// interface marked <c>[SparkDiscoverable]</c>. It replaces the seven per-kind registration
/// generators and AllFeatures' duplicate scan; <c>AddSparkFull</c> calls the generated method.
/// </summary>
/// <remarks>
/// Each kind is emitted only when the types its registration line names resolve in the compilation,
/// so a project that references only part of Spark still compiles.
/// </remarks>
[Generator(LanguageNames.CSharp)]
public class SparkDiscoveryGenerator : IncrementalGenerator
{
    internal const string SparkBuilder = "MintPlayer.Spark.Abstractions.Builder.ISparkBuilder";
    internal const string DiscoverableAttribute = "MintPlayer.Spark.Abstractions.SparkDiscoverableAttribute";
    internal const string AllFeaturesOptions = "MintPlayer.Spark.AllFeatures.SparkFullOptions";
    private const string DefaultActions = "MintPlayer.Spark.Actions.DefaultPersistentObjectActions<T>";
    private const string PersistentObjectActions = "MintPlayer.Spark.Actions.IPersistentObjectActions<T>";
    private const string CustomAction = "MintPlayer.Spark.Abstractions.Actions.ICustomAction";
    private const string Recipient = "MintPlayer.Spark.Messaging.Abstractions.IRecipient<TMessage>";
    private const string CheckpointRecipient = "MintPlayer.Spark.Messaging.Abstractions.ICheckpointRecipient<TMessage>";
    private const string CronJob = "MintPlayer.Spark.Cron.ISparkCronJob";
    private const string Interceptor = "MintPlayer.Spark.Abstractions.Interceptors.ISparkInterceptor";
    private const string SubscriptionWorker = "MintPlayer.Spark.SubscriptionWorker.SparkSubscriptionWorker<T>";
    private const string RowPolicy = "MintPlayer.Spark.Abstractions.Authorization.IRowPolicy";

    public override void Initialize(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<Settings> settingsProvider)
    {
        var registrationsProvider = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, ct) => node is ClassDeclarationSyntax classDecl &&
                    classDecl.BaseList != null &&
                    classDecl.BaseList.Types.Count > 0,
                transform: static (ctx, ct) =>
                {
                    if (ctx.Node is not ClassDeclarationSyntax classDeclaration)
                        return default;

                    var classSymbol = ctx.SemanticModel.GetDeclaredSymbol(classDeclaration, ct) as INamedTypeSymbol;
                    if (classSymbol == null || classSymbol.IsAbstract)
                        return default;

                    var found = Classify(classSymbol);
                    return found.Count > 0 ? found.ToArray() : default;
                })
            .Where(static x => x != null)
            .SelectMany(static (x, ct) => x!)
            .Collect();

        var gatesProvider = context.CompilationProvider
            .Select(static (compilation, ct) => new DiscoveryGates
            {
                HasBuilder = compilation.GetTypeByMetadataName(SparkBuilder) != null,
                Actions = compilation.GetTypeByMetadataName("MintPlayer.Spark.Actions.DefaultPersistentObjectActions`1") != null,
                CustomActions = compilation.GetTypeByMetadataName(CustomAction) != null,
                Recipients = compilation.GetTypeByMetadataName("MintPlayer.Spark.Messaging.Abstractions.IRecipient`1") != null,
                CronJobs = compilation.GetTypeByMetadataName(CronJob) != null,
                Migrations = compilation.GetTypeByMetadataName(ReferencedMigrationsReader.MigrationInterfaceMetadataName) != null,
                Interceptors = compilation.GetTypeByMetadataName("MintPlayer.Spark.Abstractions.Interceptors.SparkBuilderInterceptorExtensions") != null,
                // The registration names SparkSubscriptionExtensions, which lives in the runtime package, not
                // the abstractions that declare the base class.
                SubscriptionWorkers = compilation.GetTypeByMetadataName("MintPlayer.Spark.SubscriptionWorker.SparkSubscriptionWorker`1") != null
                    && compilation.GetTypeByMetadataName("MintPlayer.Spark.SubscriptionWorker.SparkSubscriptionExtensions") != null,
                RowPolicies = compilation.GetTypeByMetadataName("MintPlayer.Spark.Extensions.SparkBuilderRowPolicyExtensions") != null,
                Discoverable = compilation.GetTypeByMetadataName(DiscoverableAttribute) != null,
                AllFeatures = compilation.GetTypeByMetadataName(AllFeaturesOptions) != null,
            });

        // Migrations that referenced packages ship (#388), registered after the app's own. Host only.
        var referencedMigrationsProvider = context.CompilationProvider
            .Select(static (compilation, ct) => ReferencedMigrationsReader.Read(compilation, ct));

        var sourceProvider = registrationsProvider
            .Combine(gatesProvider)
            .Combine(referencedMigrationsProvider)
            .Combine(settingsProvider)
            .Select(static (providers, ct) =>
            {
                var registrations = providers.Left.Left.Left;
                var gates = providers.Left.Left.Right;
                var referencedMigrations = providers.Left.Right;
                var settings = providers.Right;

                var all = registrations.Where(r => r != null).ToList();
                var ownMigrations = new HashSet<string>(
                    all.Where(r => r.Kind == DiscoveryKind.Migration).Select(r => r.TypeName), StringComparer.Ordinal);
                all.AddRange(referencedMigrations.Migrations
                    .Where(m => !ownMigrations.Contains(m.MigrationTypeName))
                    .Select(m => new DiscoveredRegistration { Kind = DiscoveryKind.Migration, TypeName = m.MigrationTypeName }));

                return (Producer)new SparkDiscoveryProducer(all, gates, settings.RootNamespace ?? "GeneratedCode");
            });

        context.ProduceCode(sourceProvider);
    }

    /// <summary>Every registration one concrete class contributes. Each filter is the one its former per-kind generator used.</summary>
    private static List<DiscoveredRegistration> Classify(INamedTypeSymbol classSymbol)
    {
        var result = new List<DiscoveredRegistration>();
        var typeName = classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // An open generic cannot be registered, and a private (or protected) nested class cannot be named.
        var nameable = !classSymbol.IsGenericType && IsNameable(classSymbol);
        var isActionsClass = classSymbol.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() == PersistentObjectActions);

        // Base-class kinds: Actions classes and subscription workers.
        for (var baseType = classSymbol.BaseType; baseType != null; baseType = baseType.BaseType)
        {
            if (!baseType.IsGenericType)
                continue;

            var definition = baseType.ConstructedFrom.ToDisplayString();
            if (definition == DefaultActions)
            {
                var entityType = baseType.TypeArguments.FirstOrDefault();
                if (entityType != null)
                {
                    result.Add(new DiscoveredRegistration
                    {
                        Kind = DiscoveryKind.Actions,
                        TypeName = typeName,
                        ServiceTypeName = entityType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    });
                }
                break;
            }

            if (definition == SubscriptionWorker)
            {
                result.Add(new DiscoveredRegistration { Kind = DiscoveryKind.SubscriptionWorker, TypeName = typeName });
                break;
            }
        }

        var checkpointMessages = new HashSet<string>(StringComparer.Ordinal);
        foreach (var iface in classSymbol.AllInterfaces)
        {
            if (iface.IsGenericType && iface.ConstructedFrom.ToDisplayString() == CheckpointRecipient)
            {
                var messageType = iface.TypeArguments.FirstOrDefault();
                if (messageType != null)
                    checkpointMessages.Add(messageType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            }
        }

        foreach (var iface in classSymbol.AllInterfaces)
        {
            var display = iface.ToDisplayString();

            // An open generic or a private nested class cannot be named in the generated registrations (found by the
            // reference-data source-check job, a private nested generic ISparkCronJob registered by hand).
            if (!nameable && (display == CustomAction || display == CronJob || display == ReferencedMigrationsReader.MigrationInterfaceMetadataName))
                continue;

            if (display == CustomAction)
            {
                result.Add(new DiscoveredRegistration { Kind = DiscoveryKind.CustomAction, TypeName = typeName });
            }
            else if (display == CronJob)
            {
                result.Add(new DiscoveredRegistration { Kind = DiscoveryKind.CronJob, TypeName = typeName });
            }
            else if (display == ReferencedMigrationsReader.MigrationInterfaceMetadataName)
            {
                result.Add(new DiscoveredRegistration { Kind = DiscoveryKind.Migration, TypeName = typeName });
            }
            else if (display == Interceptor)
            {
                // An Actions class implementing interceptors needs no registration: the framework runs it as its type's interceptor.
                if (nameable && !isActionsClass)
                    result.Add(new DiscoveredRegistration { Kind = DiscoveryKind.Interceptor, TypeName = typeName });
            }
            else if (display == RowPolicy)
            {
                if (nameable)
                    result.Add(new DiscoveredRegistration { Kind = DiscoveryKind.RowPolicy, TypeName = typeName });
            }
            else if (iface.IsGenericType && iface.ConstructedFrom.ToDisplayString() == Recipient)
            {
                var messageType = iface.TypeArguments.FirstOrDefault();
                if (messageType != null)
                {
                    var messageTypeName = messageType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    result.Add(new DiscoveredRegistration
                    {
                        Kind = DiscoveryKind.Recipient,
                        TypeName = typeName,
                        ServiceTypeName = messageTypeName,
                        IsCheckpointRecipient = checkpointMessages.Contains(messageTypeName),
                    });
                }
            }

            // Not exclusive with the kinds above: a contract interface may also be one of them.
            if (nameable && TryGetDiscoverableLifetime(iface, out var lifetime))
            {
                result.Add(new DiscoveredRegistration
                {
                    Kind = DiscoveryKind.Discoverable,
                    TypeName = typeName,
                    ServiceTypeName = iface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    Lifetime = lifetime,
                });
            }
        }

        return result;
    }

    /// <summary>Whether code at namespace level in the same assembly can name the type: it and every containing type are public or internal.</summary>
    private static bool IsNameable(INamedTypeSymbol type)
    {
        if (type.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
            return false;

        for (var outer = type.ContainingType; outer != null; outer = outer.ContainingType)
        {
            // A class nested in an open generic is itself open.
            if (outer.IsGenericType || outer.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Whether <paramref name="iface"/> (its open definition, for a constructed generic) carries
    /// <c>[SparkDiscoverable]</c>, and with which lifetime. Attributes are read from metadata too,
    /// so an interface declared in a referenced package counts.
    /// </summary>
    private static bool TryGetDiscoverableLifetime(INamedTypeSymbol iface, out int lifetime)
    {
        lifetime = 1; // ServiceLifetime.Scoped
        foreach (var attribute in iface.OriginalDefinition.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != DiscoverableAttribute)
                continue;

            foreach (var named in attribute.NamedArguments)
            {
                if (named.Key == "Lifetime" && named.Value.Value is int value)
                    lifetime = value;
            }
            return true;
        }
        return false;
    }
}
