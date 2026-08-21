using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Configuration;
using MintPlayer.Spark.Converters;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Session;
using Raven.Client.Json.Serialization.NewtonsoftJson;
using Raven.Client.ServerWide.Operations;
using System.Reflection;

namespace MintPlayer.Spark;

public static class SparkExtensions
{
    public static IServiceCollection AddSpark(this IServiceCollection services, IConfiguration configuration, Action<ISparkBuilder> configure)
    {
        var builder = new SparkBuilder(services, configuration);
        configuration.GetSection("Spark").Bind(builder.Options);
        return services.AddSparkCore(builder, configure);
    }

    public static IServiceCollection AddSpark(this IServiceCollection services, Action<ISparkBuilder> configure)
    {
        var builder = new SparkBuilder(services);
        return services.AddSparkCore(builder, configure);
    }

    private static IServiceCollection AddSparkCore(this IServiceCollection services, SparkBuilder builder, Action<ISparkBuilder> configure)
    {
        var options = builder.Options;

        // Expose the bound SparkOptions to DI so services (e.g. BreadcrumbResolver) can read
        // configuration. Same instance the builder holds, so later configure() tweaks apply.
        services.AddSingleton(options);

        // Register authorization (required by UseSpark → UseAuthorization)
        services.AddAuthorization();

        // Register antiforgery (required by Spark's POST/PUT/DELETE endpoints)
        services.AddAntiforgery(opt => opt.HeaderName = "X-XSRF-TOKEN");

        // Ensure HttpContextAccessor is available (needed for RequestCultureResolver)
        services.AddHttpContextAccessor();

        // Register the Spark services
        services.AddSparkServices();

        // Open generics are skipped by the [Register] generator, so the row-rule facade is wired
        // here. It is the seam an application reaches for to apply an entity's row rule from its own
        // controllers and jobs (#301).
        services.AddScoped(typeof(Abstractions.Authorization.ISparkRowRule<>), typeof(Services.SparkRowRule<>));

        // The model synchronizer rewrites App_Data/Model/*.json from the entity classes. It is a
        // build-time tool, so outside Development it is not in the container at all — there is
        // nothing to resolve rather than a guard to get past.
        //
        // This must happen here and not in a CreateBuilder-style factory: AddSparkServices() above
        // runs later than any such factory would, and its registration would win GetRequiredService,
        // silently reducing the gate to a no-op.
        if (GetRegistrationTimeEnvironment(services)?.IsDevelopment() == true)
            services.AddSingleton<IModelSynchronizer, ModelSynchronizer>();

        // Default IAccessControl is fail-closed (deny everything). Apps opt into a
        // real authorization model via spark.AddAuthorization() (from the Spark
        // Authorization package) or into "no authorization" mode via
        // spark.AllowAnonymousAccess(). Either opt-in registers an IAccessControl
        // *after* this one, and DI resolves the last registration, so the deny-all
        // default only applies when neither opt-in was called. Per R2-H1: this
        // closes the silent fail-open path where AddSpark without AddAuthorization
        // accepted every request.
        services.AddScoped<IAccessControl, DenyAllAccessControl>();

        services.AddSingleton<IDocumentStore>(sp =>
        {
            var store = new DocumentStore
            {
                Urls = options.RavenDb.Urls.Length > 0 ? options.RavenDb.Urls : ["http://localhost:8080"],
                Database = options.RavenDb.Database,
            };

            store.Conventions.UseNaturalIds().UseGeneratedIds();

            // Register custom JSON converters for RavenDB document serialization
            store.Conventions.Serialization = new NewtonsoftJsonSerializationConventions
            {
                CustomizeJsonSerializer = serializer =>
                {
                    serializer.Converters.Add(new ColorNewtonsoftJsonConverter());
                }
            };

            store.Initialize();

            // Wait for RavenDB to become available (handles container startup ordering in docker-compose, etc.)
            WaitForRavenDbConnection(store, options.RavenDb);

            var hostEnvironment = sp.GetRequiredService<IHostEnvironment>();
            if (hostEnvironment.IsDevelopment() || options.RavenDb.EnsureDatabaseCreated)
            {
                var databaseNames = store.Maintenance.Server.Send(new GetDatabaseNamesOperation(0, int.MaxValue));
                if (!databaseNames.Contains(options.RavenDb.Database))
                {
                    store.Maintenance.Server.Send(new CreateDatabaseOperation(o =>
                        o.Regular(options.RavenDb.Database).WithReplicationFactor(1)
                    ));
                }
            }

            return store;
        });

        // Request-scoped Raven sessions. One session per HTTP request, disposed when the
        // DI scope ends. MaxNumberOfRequestsPerSession stays at Raven's default (30) — if
        // a single method needs more headroom, use SessionExtensions.IgnoreMaxRequests().
        services.AddScoped<IAsyncDocumentSession>(sp =>
            sp.GetRequiredService<IDocumentStore>().OpenAsyncSession());

        services.AddScoped<IDocumentSession>(sp =>
            sp.GetRequiredService<IDocumentStore>().OpenSession());

        // Let modules register their services
        configure(builder);

        // Store the registry in DI so UseSpark/MapSpark can access it
        services.AddSingleton(builder.Registry);

        return services;
    }

    /// <summary>
    /// Reads the host environment at <em>registration</em> time, without building a provider.
    /// <para>
    /// The web host registers its environment as a singleton instance, so it can be read straight
    /// off the descriptor. Resolving it from a factory lambda instead — the way the document store
    /// does — would be too late to decide what gets registered. Returns <see langword="null"/> for a
    /// bare <see cref="ServiceCollection"/>, which has no such descriptor.
    /// </para>
    /// </summary>
    private static IHostEnvironment? GetRegistrationTimeEnvironment(IServiceCollection services)
        => (services.LastOrDefault(d => d.ServiceType == typeof(IHostEnvironment))?.ImplementationInstance
            ?? services.LastOrDefault(d => d.ServiceType == typeof(IWebHostEnvironment))?.ImplementationInstance)
            as IHostEnvironment;

    /// <summary>
    /// Registers the SparkContext implementation for this application.
    /// </summary>
    public static ISparkBuilder UseContext<TContext>(this ISparkBuilder builder)
        where TContext : SparkContext
    {
        builder.Services.AddScoped<SparkContext, TContext>();
        return builder;
    }

    /// <summary>
    /// Registers entity-specific Actions class for customizing CRUD behavior.
    /// Used internally by the source generator.
    /// </summary>
    public static IServiceCollection AddSparkActions<TActions, TEntity>(this IServiceCollection services)
        where TActions : class, IPersistentObjectActions<TEntity>
        where TEntity : class
    {
        services.AddScoped<IPersistentObjectActions<TEntity>, TActions>();
        services.AddScoped<TActions>();
        return services;
    }

    /// <summary>
    /// Configures Spark middleware, indexes, and all registered module middleware.
    /// Call after UseRouting(). Do NOT call UseAuthentication/UseAuthorization/UseAntiforgery separately
    /// when using this method — they are added automatically if authentication is configured.
    /// </summary>
    public static IApplicationBuilder UseSpark(this IApplicationBuilder app)
    {
        var registry = app.ApplicationServices.GetRequiredService<SparkModuleRegistry>();

        // Middleware that must reject a request before the cost of authenticating it is paid — a rate
        // limiter above all. No credential has been validated yet, so nothing at this stage may read
        // the principal.
        //
        // Like everything in UseSpark, this stage assumes the app called UseRouting() first (see the
        // method's doc comment), so endpoint metadata resolves and endpoint-attached policies apply.
        // That is a contract, not a check: UseRouting lives outside UseSpark, so this stage is on the
        // same side of routing as the rest of UseSpark either way, and UseAuthorization below carries
        // the identical requirement for [Authorize] — which ASP.NET Core itself leaves unguarded.
        registry.ApplyMiddleware(app, SparkMiddlewareStage.BeforeAuthentication);

        // Any registered credential is a reason to authenticate, not just Identity. An app whose
        // only callers are machines — client certificates, or bearer tokens from the identity
        // provider — registers no user type, and gating on that alone would leave its middleware
        // out entirely, so every such caller would arrive anonymous.
        if (registry.IdentityUserType != null || registry.CredentialSchemes.Count > 0)
        {
            app.UseAuthentication();
        }

        app.UseAuthorization();

        // Antiforgery validation for mutating requests that carry IAntiforgeryMetadata.
        //
        // Runs BEFORE the built-in UseAntiforgery() so this middleware can call
        // IAntiforgery.ValidateRequestAsync before FormFeature's "unvalidated" guard
        // gets set. After successful validation we set IAntiforgeryValidationFeature to
        // "validated" so (a) the built-in middleware and FormFeature treat the request
        // as already checked and (b) EndpointMiddleware doesn't throw
        // "contains anti-forgery metadata, but a middleware was not found".
        //
        // The built-in UseAntiforgery() was narrowed in 8.0.1 to validate ONLY form-content
        // bodies — Spark's JSON API is not protected by it alone. This middleware closes
        // that gap for any mutating HTTP method (POST/PUT/PATCH/DELETE) whose endpoint has
        // IAntiforgeryMetadata.RequiresValidation = true.
        app.UseSparkAntiforgery();

        // Keep the built-in middleware registered — EndpointMiddleware uses its presence as a
        // "antiforgery was wired" probe when the endpoint has IAntiforgeryMetadata. For
        // non-form mutating requests that pass Spark's validation above, it's a no-op.
        app.UseAntiforgery();

        // WebSockets must be enabled BEFORE the origin guard below. The guard keys off
        // context.WebSockets.IsWebSocketRequest, which only reports true once the WebSocket
        // middleware has inspected the upgrade and populated IHttpWebSocketFeature. Registering
        // the guard ahead of UseWebSockets() made IsWebSocketRequest always false there, so the
        // guard never fired — silently disabling the CSWSH protection it exists to provide.
        app.UseWebSockets();

        // R2-H5: enforce same-origin on WebSocket upgrades. ASP.NET Core's default
        // is no origin check at all, which means an attacker page can open a WS to
        // /spark/queries/{id}/stream and ride the victim's ambient cookies (CSWSH).
        // We accept requests with no Origin header (non-browser clients) and
        // requests whose Origin host matches the request's Host. Cross-origin WS
        // remains an explicit opt-in via SparkWebSocketAllowedOrigins (config or
        // builder ext) once that surface materializes — for now, fail-closed.
        app.Use(async (context, next) =>
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                var origin = context.Request.Headers.Origin.ToString();
                if (!string.IsNullOrEmpty(origin) &&
                    Uri.TryCreate(origin, UriKind.Absolute, out var originUri) &&
                    !string.Equals(originUri.Host, context.Request.Host.Host, StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
            }
            await next(context);
        });

        // Generate XSRF-TOKEN cookie on each response for Angular's HttpClient
        app.Use(async (context, next) =>
        {
            var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
            var tokens = antiforgery.GetAndStoreTokens(context);
            if (tokens.RequestToken != null)
            {
                context.Response.Cookies.Append("XSRF-TOKEN", tokens.RequestToken, new CookieOptions
                {
                    // HttpOnly=false is intentional — the Angular client reads this cookie
                    // and echoes it back in X-XSRF-TOKEN (double-submit pattern).
                    HttpOnly = false,
                    SameSite = SameSiteMode.Strict,
                    // Secure=IsHttps so the token is never sent over plain HTTP in production,
                    // but local HTTP development still works.
                    Secure = context.Request.IsHttps,
                    Path = "/"
                });
            }
            await next(context);
        });

        app.UseMiddleware<SparkMiddleware>();

        // Create RavenDB indexes
        CreateSparkIndexes(app, registry.ResolveIndexAssemblies());

        // After CreateSparkIndexes, because the projection type and index name feed the model hash
        // and the index registry is populated there. Before any request is served: a drifted model
        // shows up as missing columns and values silently dropped on save, which reads as data loss
        // rather than a configuration mistake.
        VerifySparkModelHash(app);

        ReportSecurityPosture(app);

        // Run module-specific middleware/startup tasks
        registry.ApplyMiddleware(app, SparkMiddlewareStage.AfterSpark);

        return app;
    }

    /// <summary>
    /// Configures Spark middleware with additional options.
    /// Call after UseRouting(). Do NOT call UseAuthentication/UseAuthorization/UseAntiforgery separately.
    /// </summary>
    public static IApplicationBuilder UseSpark(this IApplicationBuilder app, Action<UseSparkOptions> configure)
    {
        app.UseSpark();

        var options = new UseSparkOptions { App = app };
        configure(options);

        return app;
    }

    /// <summary>
    /// Maps all Spark endpoints, including any registered module endpoints (authorization, replication, etc.).
    /// </summary>
    public static IEndpointRouteBuilder MapSpark(this IEndpointRouteBuilder endpoints)
    {
        var registry = endpoints.ServiceProvider.GetRequiredService<SparkModuleRegistry>();

        // Map all core Spark endpoints (source-generated from endpoint classes)
        endpoints.MapSparkCoreEndpoints();

        // Map module-specific endpoints (authorization, replication, etc.)
        registry.MapEndpoints(endpoints);

        return endpoints;
    }

    private static void WaitForRavenDbConnection(IDocumentStore store, Configuration.RavenDbOptions ravenDbOptions)
    {
        var maxRetries = ravenDbOptions.MaxConnectionRetries;
        if (maxRetries <= 0) return;

        var delay = TimeSpan.FromSeconds(Math.Max(ravenDbOptions.RetryDelaySeconds, 1));

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                store.Maintenance.Server.Send(new GetDatabaseNamesOperation(0, 1));
                if (attempt > 1)
                {
                    Console.WriteLine($"Successfully connected to RavenDB after {attempt} attempts.");
                }
                return;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                Console.WriteLine($"Waiting for RavenDB to become available (attempt {attempt}/{maxRetries}): {ex.Message}");
                Thread.Sleep(delay);
            }
        }

        // Final attempt — let the exception propagate if it still fails
        store.Maintenance.Server.Send(new GetDatabaseNamesOperation(0, 1));
    }

    /// <summary>
    /// Populates <paramref name="indexCatalog"/> from the index and projection types declared in
    /// <paramref name="targetAssembly"/>. Pure reflection — no database, no host, no DI. The caller
    /// freezes once every assembly is registered.
    /// <para>
    /// Separated from <see cref="CreateSparkIndexes"/> so the offline paths (model synchronization
    /// and the startup model-hash check) can populate the catalog without a live
    /// <c>IDocumentStore</c>. Both consult the catalog for projection types and index names, and an
    /// unpopulated catalog does not fail — it silently emits projection types as their own model
    /// files and skips the query-type merge. Wrong output, no error, which is why this must run.
    /// </para>
    /// <para>
    /// Deliberately does not swallow exceptions: a catalog that failed to populate has to fail the
    /// run. Only the database call in <see cref="CreateSparkIndexes"/> is best-effort.
    /// </para>
    /// </summary>
    internal static void PopulateIndexCatalog(IIndexCatalog indexCatalog, Assembly targetAssembly)
    {
        PopulateIndexTypes(indexCatalog, targetAssembly);
        PopulateProjectionTypes(indexCatalog, targetAssembly);
    }

    /// <summary>
    /// Registers the index types declared in <paramref name="targetAssembly"/>.
    /// <para>
    /// Separate from projection registration so callers spanning several assemblies can register
    /// every index before any projection. A projection resolves its index by name, so with a single
    /// combined pass a projection in one assembly over an index in a later-scanned assembly would
    /// fail to resolve — and the failure is only a console warning.
    /// </para>
    /// </summary>
    internal static void PopulateIndexTypes(IIndexCatalog indexCatalog, Assembly targetAssembly)
    {
        var indexTypes = ReflectionCache.GetOrAdd<(string Op, Assembly Asm), IReadOnlyList<Type>>(
            ("SparkMiddleware.IndexTypes", targetAssembly),
            static k => GetLoadableTypes(k.Asm)
                .Where(t => !t.IsAbstract && IsAbstractIndexCreationTask(t))
                .ToArray());

        foreach (var indexType in indexTypes)
        {
            indexCatalog.RegisterIndex(indexType);
        }
    }

    /// <summary>Registers the <c>[FromIndex]</c> projection types declared in <paramref name="targetAssembly"/>.</summary>
    internal static void PopulateProjectionTypes(IIndexCatalog indexCatalog, Assembly targetAssembly)
    {
        var projectionTypes = ReflectionCache.GetOrAdd<(string Op, Assembly Asm), IReadOnlyList<Type>>(
            ("SparkMiddleware.ProjectionTypes", targetAssembly),
            static k => GetLoadableTypes(k.Asm)
                .Where(t => t.GetCachedCustomAttribute<FromIndexAttribute>() != null)
                .ToArray());

        foreach (var projectionType in projectionTypes)
        {
            var attr = projectionType.GetCachedCustomAttribute<FromIndexAttribute>()!;
            indexCatalog.RegisterProjection(projectionType, attr.IndexType);
        }
    }

    /// <summary>
    /// The types of an assembly, keeping what loaded when some types cannot.
    /// <para>
    /// <c>Assembly.GetTypes()</c> walks the entire metadata tables and throws if any type fails to
    /// load — typically an optional peer dependency that is simply absent. That is a deployment fact
    /// rather than a Spark defect, and now that several assemblies are scanned, one such assembly
    /// must not stop an application from starting. A genuinely malformed index type still throws out
    /// of registration, so a real failure is not masked.
    /// </para>
    /// <para>
    /// Callers cache this through <c>ReflectionCache</c>, which stores a throwing factory and
    /// re-throws it for the lifetime of the process — so the catch has to live here, inside the
    /// factory, not around the cache lookup.
    /// </para>
    /// </summary>
    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            var loaded = ex.Types.Where(t => t is not null).Select(t => t!).ToArray();
            Console.WriteLine(
                $"Warning: {assembly.GetName().Name} has {ex.Types.Length - loaded.Length} type(s) that could not be " +
                $"loaded; scanning the {loaded.Length} that did. First loader error: {ex.LoaderExceptions.FirstOrDefault()?.Message}");
            return loaded;
        }
    }

    private static void VerifySparkModelHash(IApplicationBuilder app)
    {
        var hostEnvironment = app.ApplicationServices.GetRequiredService<IHostEnvironment>();
        var indexCatalog = app.ApplicationServices.GetRequiredService<IIndexCatalog>();

        using var scope = app.ApplicationServices.CreateScope();
        var sparkContext = scope.ServiceProvider.GetService<SparkContext>();
        if (sparkContext is null)
        {
            // No context registered means no model to verify — an app that never called
            // UseContext<T>(). Nothing to check rather than a failure.
            return;
        }

        ModelHashVerifier.Verify(
            sparkContext.GetType(),
            indexCatalog,
            hostEnvironment.ContentRootPath,
            hostEnvironment.IsDevelopment(),
            Console.WriteLine);
    }

    /// <summary>
    /// Prints which rights an anonymous caller holds, on every startup.
    /// <para>
    /// Follows the principle <c>ModelHashVerifier</c> states — warned on every startup, never once —
    /// and prints the negative case explicitly. "Anonymous callers can reach nothing" is the whole
    /// point: silence is indistinguishable from the check not running, so an operator reading a log
    /// could not tell a closed surface from a summary that was never emitted.
    /// </para>
    /// <para>
    /// Logs rather than throws. Malformed configuration is refused at load, because the file then
    /// does not say what its author thinks it says; a genuinely public API is a policy decision an
    /// application is entitled to make, and refusing to start over it would be wrong.
    /// </para>
    /// </summary>
    private static void ReportSecurityPosture(IApplicationBuilder app)
    {
        var reporter = app.ApplicationServices.GetService<ISecurityPostureReporter>();
        if (reporter is null)
            return;   // No authorization package, so no posture to describe.

        var logger = app.ApplicationServices.GetService<ILoggerFactory>()
            ?.CreateLogger("MintPlayer.Spark.Security");
        if (logger is null)
            return;

        var posture = reporter.Describe();

        if (posture.AnonymouslyReachable.Count == 0)
        {
            logger.LogInformation("Spark security: anonymous callers can reach nothing.");
        }
        else
        {
            logger.LogWarning(
                "Spark security: anonymous callers can reach {Count} right(s) — {Rights}.",
                posture.AnonymouslyReachable.Count,
                string.Join(", ", posture.AnonymouslyReachable));
        }

        foreach (var warning in posture.Warnings)
            logger.LogWarning("Spark security: {Warning}", warning);
    }

    private static void CreateSparkIndexes(IApplicationBuilder app, IReadOnlyList<Assembly> assemblies)
    {
        var documentStore = app.ApplicationServices.GetRequiredService<IDocumentStore>();

        if (assemblies.Count == 0)
        {
            Console.WriteLine("Warning: Could not determine any assembly to scan for index creation.");
            return;
        }

        // Materialize every assembly before the first database call. Catalog population is a
        // correctness precondition — the model-hash check runs straight after this and must see a
        // complete catalog even when RavenDB is unreachable and the deployment below fails.
        //
        // Indexes across all assemblies first, then projections across all of them: a projection
        // resolves its index by name, so a projection in one assembly over an index in another must
        // not depend on which assembly was scanned first.
        //
        // Freezing runs the [DefaultIndex] validation, so an ambiguous default fails startup here —
        // before any query can resolve through it.
        var indexCatalog = app.ApplicationServices.GetRequiredService<IIndexCatalog>();

        foreach (var assembly in assemblies)
            PopulateIndexTypes(indexCatalog, assembly);

        foreach (var assembly in assemblies)
            PopulateProjectionTypes(indexCatalog, assembly);

        indexCatalog.Freeze();

        // Deployment is best-effort, but per assembly: one unreachable or broken module must not
        // cost every other module its indexes, which is what a single surrounding catch did.
        foreach (var assembly in assemblies)
        {
            try
            {
                IndexCreation.CreateIndexes(assembly, documentStore);
                Console.WriteLine($"RavenDB indexes created/updated from assembly: {assembly.GetName().Name}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error creating RavenDB indexes from {assembly.GetName().Name}: {ex.Message}");
            }
        }
    }

    private static bool IsAbstractIndexCreationTask(Type type)
    {
        var current = type;
        while (current != null && current != typeof(object))
        {
            if (current.IsGenericType)
            {
                var genericDef = current.GetGenericTypeDefinition();
                if (genericDef == typeof(AbstractIndexCreationTask<>) ||
                    genericDef == typeof(AbstractMultiMapIndexCreationTask<>))
                {
                    return true;
                }
            }
            current = current.BaseType;
        }
        return false;
    }

}

public partial class SparkMiddleware
{
    [Inject] private readonly RequestDelegate next;

    public async Task InvokeAsync(HttpContext context)
    {
        // Pre-processing logic
        Console.WriteLine("Before the next middleware");

        // Call the next middleware in the pipeline
        await next(context);

        // Post-processing logic
        Console.WriteLine("After the next middleware");
    }
}
