using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Exceptions;
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
    /// <summary>
    /// The CORS policy Spark's own endpoints answer with by default.
    /// </summary>
    /// <remarks>
    /// Public so an application or a library can name it — to reuse it on an endpoint of its own with
    /// <c>RequireCors</c>, or to recognise it. Opting a Spark endpoint out is
    /// <c>.WithMetadata(new DisableCorsAttribute())</c> — ⚠️ there is no <c>.DisableCors()</c> builder
    /// extension, only the attribute, which is easy to assume otherwise given <c>RequireCors</c> exists;
    /// there is nothing to un-register.
    /// </remarks>
    public const string SparkCorsPolicy = "SparkCors";

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

        // CORS services, always — for the same reason antiforgery is registered always, and with the
        // same consequence if it is not. Registering them is what makes `RequireCors` *safe to write*
        // on any endpoint: ASP.NET fails a request whose endpoint carries CORS metadata when no CORS
        // middleware is present, and the middleware in turn throws at startup if AddCors was never
        // called. A module should be able to declare what an endpoint needs without also having to
        // arrange the pipeline for it — the identity provider could not, and `/connect/token` threw on
        // every request as a result.
        services.AddCors(cors => cors.AddPolicy(SparkCorsPolicy, policy =>
        {
            // ⚠️ A NAMED policy, never AddDefaultPolicy. A default policy is last-write-wins: Spark
            // registering one would silently clobber an application's own — or be clobbered by it —
            // depending on whether the app called AddCors before or after AddSpark. An application's
            // controllers would stop working cross-origin because it added Spark, and the outcome
            // would depend on call order. A named policy collides with nothing.
            //
            // `AllowAnyOrigin` emits `Access-Control-Allow-Origin: *` rather than echoing the caller's
            // origin. That is the honest signal for what this is — a public, credential-free read —
            // and it makes the dangerous combination impossible by construction: ASP.NET refuses
            // `AllowCredentials` alongside any-origin, so nobody can later widen this into
            // cross-origin access to a signed-in user's data without first confronting that.
            //
            // ⚠️ What this does and does not expose. A cross-origin request carries no cookies unless
            // the response also grants credentials, so what a browser page can read here is the
            // ANONYMOUS view — which any HTTP client could already fetch directly, without a browser
            // and without CORS. The exception worth knowing is a Spark app on a private network: a
            // public page a user visits can read its anonymous surface through their browser, which it
            // could not reach on its own. An intranet deployment that cares should turn this off.
            policy.AllowAnyOrigin()
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        }));

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

        // IAccessControl, its loader, the claims-based group provider and the posture reporter all
        // come from AddSparkServices() above, because they are ordinary [Register]ed core services
        // now. There is no three-way default any more: authorization is not opt-in, so there is no
        // "neither opt-in was called" state to have a fallback for.
        //
        // [SparkAuthorize] on a controller action or minimal-API endpoint is wired here rather than
        // by the Controllers module, because the attribute belongs to whoever owns security.json and
        // it applies equally to a RequireAuthorization(). Singleton is ASP.NET Core's convention for
        // authorization handlers; the handler resolves the scoped IAccessControl per evaluation.
        services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, Services.SparkAuthorizeHandler>();

        services.AddSingleton<IDocumentStore>(sp =>
        {
            var store = new DocumentStore
            {
                Urls = options.RavenDb.Urls.Length > 0 ? options.RavenDb.Urls : ["http://localhost:8080"],
                Database = options.RavenDb.Database,
            };

            // One call, shared with the test drivers. Configuring a store by hand here is how
            // production and the suite drifted apart -- see SparkStoreConfiguration.
            store.ApplySparkConventions();

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
    /// <para>
    /// Also declares the context's own assembly for index and projection discovery. Discovery
    /// otherwise starts from <see cref="Assembly.GetEntryAssembly"/>, which is the application only
    /// when the application is the process entry point: under an in-process test host
    /// (<c>WebApplicationFactory</c>) the entry assembly is the test runner, so the application's
    /// indexes were neither deployed nor catalogued — and the empty catalog silently dropped the
    /// <c>querytype</c>/<c>index</c> lines from every projection-backed entity's model shape, so the
    /// startup hash check rejected a model that <c>--spark-verify-model</c> had just accepted.
    /// The context's assembly is the right anchor because it is the assembly the model shape is
    /// derived from, and the one the index generator emits into.
    /// </para>
    /// </summary>
    public static ISparkBuilder UseContext<TContext>(this ISparkBuilder builder)
        where TContext : SparkContext
    {
        builder.Services.AddScoped<SparkContext, TContext>();
        builder.Registry.AddIndexAssembly(typeof(TContext).Assembly);
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

        // CORS. Two branches, because Spark's own endpoints and everyone else's have different
        // defaults — and a single middleware cannot express that, since the policy it is constructed
        // with becomes the default for every endpoint that carries no metadata of its own.
        //
        // ⚠️ Before UseAuthentication deliberately. A CORS preflight is an unauthenticated OPTIONS
        // request that carries no credentials by definition; running it after the authentication and
        // authorization stages invites those stages to refuse the preflight for a request the browser
        // has not made yet, and the failure surfaces as an opaque cross-origin error rather than as a
        // 401 anyone can read.
        app.UseWhen(
            // Spark's own API: the policy applies by default, so every framework endpoint answers with
            // `Access-Control-Allow-Origin: *` without anyone opting in. An endpoint that does not want
            // it opts OUT with `.WithMetadata(new DisableCorsAttribute())` — there is no `.DisableCors()`
            // extension, only the attribute — and one that wants a different policy names it with
            // `.RequireCors(...)` — endpoint metadata beats the branch's policy either way.
            context => context.Request.Path.StartsWithSegments(Endpoints.SparkGroup.Prefix),
            branch => branch.UseCors(SparkCorsPolicy));

        app.UseWhen(
            // Everything else — a library's endpoints outside the Spark prefix (the identity provider's
            // `/connect` and `/.well-known`), and the application's own controllers. No policy name, so
            // nothing is granted by default and each endpoint opts IN with `RequireCors`.
            //
            // ⚠️ An application that registers its own DEFAULT policy still gets it here, which is the
            // point: that is the app saying "everywhere", and Spark has no business overriding it.
            context => !context.Request.Path.StartsWithSegments(Endpoints.SparkGroup.Prefix),
            branch => branch.UseCors());

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
        // ⚠️ The comment that used to sit here said the built-in UseAntiforgery() "was narrowed in
        // 8.0.1 to validate ONLY form-content bodies". That was wrong on both counts, and it was
        // wrong in a way that flattered the design: there was no 8.0.1 change, and the middleware
        // has never keyed on content type. It skips by HTTP METHOD — Shared/HttpExtensions.cs
        // IsValidHttpMethodForForm is POST/PUT/PATCH — so a JSON POST is validated by it.
        //
        // The real reasons Spark ships its own gate, both measured against release/11.0:
        //   1. AntiforgeryMiddleware.InvokeAwaited VALIDATES BUT NEVER REJECTS. It records the
        //      verdict on IAntiforgeryValidationFeature and calls _next either way. Rejection is
        //      delegated to whoever consumes the form — RequestDelegateFactory for a minimal API
        //      that binds one, an MVC filter for a controller. An endpoint that binds no form has
        //      no such consumer, so a failed check is recorded and then ignored.
        //   2. It never runs for DELETE at all, which the method filter above excludes.
        // Spark's gate covers POST/PUT/PATCH/DELETE and short-circuits with a 400 itself.
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

        // One place turns a raised retry into its 449 envelope, for every endpoint.
        //
        // The ACCEPT half of a retry has been centralised since M0 (RetryScope); this is the EMIT
        // half, which was a `catch (SparkRetryActionException)` copy-pasted into nine endpoints with
        // nothing connecting it to the accept half. Three endpoints shipped with one half and not the
        // other, and nobody noticed until a hook finally prompted.
        //
        // ⚠️ Deliberately registered here rather than earlier: it must wrap endpoint execution, and
        // everything above it — authentication, antiforgery, the origin guard — should run and fail
        // on its own terms. A retry raised by a hook happens well inside all of that.
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (SparkRetryActionException ex) when (!context.Response.HasStarted)
            {
                // A prompt is not an error: the server is asking the caller a question, and 449 is
                // the answer channel. Everything the hook already pushed onto the client accessor
                // rides along in the same envelope, so notifications raised before the prompt are
                // not lost.
                var client = context.RequestServices.GetRequiredService<IClientAccessor>();
                await ClientResult.Retry(client, ex).ExecuteAsync(context);
            }
        });

        app.UseMiddleware<SparkMiddleware>();

        // Create RavenDB indexes
        CreateSparkIndexes(app, registry.ResolveIndexAssemblies());

        // After CreateSparkIndexes, because the projection type and index name feed the model hash
        // and the index registry is populated there. Before any request is served: a drifted model
        // shows up as missing columns and values silently dropped on save, which reads as data loss
        // rather than a configuration mistake.
        VerifySparkModelHash(app);

        VerifySparkSecurityConfiguration(app);

        ReportSecurityPosture(app);

        // Run module-specific middleware/startup tasks
        registry.ApplyMiddleware(app, SparkMiddlewareStage.AfterSpark);

        // ⚠️ AFTER ApplyMiddleware, and that is the whole reason it is here rather than beside the
        // other verifiers above. The migration runner registers itself as an AfterSpark task, so a
        // gate placed at VerifySparkModelHash's site would refuse startup on the very run that
        // would have fixed the data — permanently, since the fix can then never run. Calling it
        // inline here is ordered by construction, unlike registering another AfterSpark action,
        // which would depend on whether the app called AddMigrations() before or after.
        VerifySparkValueObjectKeys(app);

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

    /// <summary>
    /// Refuses to start while any stored embedded row is missing its key.
    /// </summary>
    /// <remarks>
    /// See <see cref="ValueObjectKeyVerifier"/> for why this asks the database rather than the
    /// model, and why it throws rather than warns. No Development exemption, following
    /// <c>VerifySparkSecurityConfiguration</c>: the failure is data integrity, and the migration
    /// that fixes it has already had its chance to run by this point in startup.
    /// </remarks>
    private static void VerifySparkValueObjectKeys(IApplicationBuilder app)
    {
        var store = app.ApplicationServices.GetService<Raven.Client.Documents.IDocumentStore>();
        if (store is null)
            return;

        using var scope = app.ApplicationServices.CreateScope();
        var sparkContext = scope.ServiceProvider.GetService<SparkContext>();
        if (sparkContext is null)
        {
            // No context registered means no model to walk — an app that never called UseContext<T>().
            return;
        }

        ValueObjectKeyVerifier
            .VerifyAsync(sparkContext.GetType(), store, Console.WriteLine, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
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
    /// Refuses to start when <c>App_Data/security.json</c> is missing or means something other than
    /// it looks like.
    /// <para>
    /// The same shape as <see cref="VerifySparkModelHash"/> and for the same reason: serving
    /// requests against an authorization model that could not be loaded is worse than not starting,
    /// because it surfaces as an application that denies everything with no visible cause — which
    /// is indistinguishable from an application that means to.
    /// </para>
    /// <para>
    /// Explicit rather than left to the posture reporter below, which would load the file anyway.
    /// A gate that happens as a side effect of a logging call is one refactor away from being
    /// removed by someone who does not know it was load-bearing.
    /// </para>
    /// <para>
    /// Unlike the model hash there is no Development exemption. A missing model file is the normal
    /// state while a developer adds a property; a missing security file is never the normal state,
    /// and the generator that fixes it takes one command.
    /// </para>
    /// </summary>
    private static void VerifySparkSecurityConfiguration(IApplicationBuilder app)
    {
        // The command that writes the file must not be blocked by the check that requires it.
        if (Environment.GetCommandLineArgs().Contains(Extensions.SparkSecurityInitExtensions.InitFlag))
            return;

        var configuration = app.ApplicationServices
            .GetRequiredService<Abstractions.Authorization.ISecurityConfigurationLoader>()
            .GetConfiguration();

        VerifyRowPolicyDeclarations(app, configuration);

        // Same trip, different file: force the query alias index to build now. It is lazy, so a
        // duplicate alias would otherwise surface as a 500 on whichever request first needed a
        // query — in an unrelated place, long after the mistake. Here it is a startup failure that
        // names both queries.
        app.ApplicationServices.GetRequiredService<IQueryLoader>().GetQueries();
    }

    /// <summary>
    /// Refuses a type reachable by a well-known group whose row policy nobody stated.
    /// </summary>
    /// <remarks>
    /// Runs in its own scope: the services that can answer "does this type have a row rule" are
    /// request-scoped, because in a request they memoize per caller. Nothing here depends on a
    /// caller — the question is about the actions class, not the principal — so a throwaway scope is
    /// the honest way to ask it at startup rather than at the first request that would have leaked.
    /// </remarks>
    private static void VerifyRowPolicyDeclarations(
        IApplicationBuilder app, Abstractions.Authorization.SecurityConfiguration configuration)
    {
        using var scope = app.ApplicationServices.CreateScope();

        var modelLoader = scope.ServiceProvider.GetService<IModelLoader>();
        var rowSecurity = scope.ServiceProvider.GetService<IRowSecurity>();
        var actionsResolver = scope.ServiceProvider.GetService<IActionsResolver>();
        if (modelLoader is null || rowSecurity is null || actionsResolver is null)
            return;

        var problems = RowPolicyDeclarationValidator.Validate(
            configuration,
            [.. modelLoader.GetEntityTypes()],
            type => ResolveClrType(type) is { } clr && rowSecurity.HasRowRule(clr),
            type => ResolveClrType(type) is { } clr
                ? TryResolve(() => actionsResolver.ResolveForType(clr))
                : TryResolve(() => actionsResolver.ResolveByEntityName(type.Name)));

        if (problems.Count > 0)
            throw new Services.SparkSecurityConfigurationException(
                string.Join(Environment.NewLine + Environment.NewLine, problems));

        static Type? ResolveClrType(EntityTypeDefinition type)
            => string.IsNullOrEmpty(type.ClrType) ? null : SparkTypeResolver.ResolveClrType(type.ClrType);

        // A type whose actions class cannot be constructed is a different failure with its own
        // message elsewhere; it must not be reported here as an undeclared row policy.
        static object? TryResolve(Func<object?> resolve)
        {
            try { return resolve(); }
            catch { return null; }
        }
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

    /// <summary>
    /// Whether RavenDB will deploy this type as an index — the gate on Spark's own discovery.
    /// </summary>
    /// <remarks>
    /// ⚠️ This used to be an open-generic identity test against <c>AbstractIndexCreationTask&lt;&gt;</c>
    /// and <c>AbstractMultiMapIndexCreationTask&lt;&gt;</c>, which silently excluded a two-argument
    /// map-reduce index. Because this gates discovery, such an index was never even registered — so
    /// not even <c>IndexCatalog</c>'s "could not determine collection type" warning appeared — while
    /// <c>IndexCreation.CreateIndexes</c> below deployed it using RavenDB's own criterion. Spark and
    /// RavenDB have to agree on what an index is, so both now ask the same question.
    /// </remarks>
    private static bool IsAbstractIndexCreationTask(Type type) => RavenIndexHierarchy.IsIndex(type);

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
