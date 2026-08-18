namespace MintPlayer.Spark.Abstractions.Builder;

public class SparkModuleRegistry
{
    public Type? IdentityUserType { get; set; }

    private readonly Dictionary<SparkMiddlewareStage, List<Action<IApplicationBuilder>>> middlewareActions = [];
    private readonly HashSet<SparkMiddlewareStage> appliedStages = [];
    private readonly List<Action<IEndpointRouteBuilder>> endpointActions = [];
    private readonly List<SparkCredentialScheme> credentialSchemes = [];

    private readonly List<System.Reflection.Assembly> indexAssemblies = [];

    /// <summary>
    /// Declares that <paramref name="assembly"/> contains RavenDB indexes and/or
    /// <c>[FromIndex]</c> projection types that Spark must discover.
    /// <para>
    /// Index and projection discovery would otherwise see only the entry assembly, so a module
    /// shipped as a class library got neither its indexes created nor its projections registered —
    /// and an unregistered projection means queries silently return index-computed fields as null.
    /// </para>
    /// <para>
    /// Declare from inside the module's own <c>AddXxx(...)</c> body, so consumers write no code.
    /// It must NOT be declared from inside an <see cref="AddMiddleware"/> callback: those run after
    /// index creation, and long after the build-time model commands have read this list, so the
    /// declaration would be silently missed by both.
    /// </para>
    /// <para>Idempotent — declaring the same assembly twice costs nothing.</para>
    /// </summary>
    public void AddIndexAssembly(System.Reflection.Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        if (!indexAssemblies.Contains(assembly))
            indexAssemblies.Add(assembly);
    }

    /// <summary>
    /// The assemblies to scan for indexes and projections: the entry assembly first, then anything
    /// declared. The single accessor both the runtime path and the build-time model commands read,
    /// so the two cannot disagree about what the model contains.
    /// <para>
    /// Declarations <em>append</em> rather than replace. Substituting would silently drop the
    /// application's own indexes the moment it added a module that declares one.
    /// </para>
    /// </summary>
    public IReadOnlyList<System.Reflection.Assembly> ResolveIndexAssemblies()
    {
        var resolved = new List<System.Reflection.Assembly>();

        if (System.Reflection.Assembly.GetEntryAssembly() is { } entryAssembly)
            resolved.Add(entryAssembly);

        foreach (var assembly in indexAssemblies)
        {
            if (!resolved.Contains(assembly))
                resolved.Add(assembly);
        }

        return resolved;
    }

    /// <summary>
    /// Declares middleware (or a one-off startup task) that <c>UseSpark()</c> runs.
    /// <para>
    /// <paramref name="stage"/> chooses which side of <c>UseAuthentication</c> the action lands on;
    /// it defaults to <see cref="SparkMiddlewareStage.AfterSpark"/>, which is where every registration
    /// ran before stages existed. Within a stage, actions run in registration order.
    /// </para>
    /// <para>
    /// Registering into a stage that <see cref="ApplyMiddleware"/> has already run throws. That
    /// combination is unsatisfiable — the pipeline is past the point the action asked for — and
    /// without the guard it is a silent no-op, the same failure mode
    /// <see cref="AddIndexAssembly"/> documents for declarations that arrive too late.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="stage"/> has already been applied.</exception>
    public void AddMiddleware(
        Action<IApplicationBuilder> action,
        SparkMiddlewareStage stage = SparkMiddlewareStage.AfterSpark)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (appliedStages.Contains(stage))
        {
            throw new InvalidOperationException(
                $"Middleware was registered for the '{stage}' stage after that stage had already been " +
                "applied, so it would never run. Register it from the module's AddXxx(...) body — " +
                "during service configuration — rather than from inside another AddMiddleware " +
                "callback, which runs while the pipeline is being built.");
        }

        if (!middlewareActions.TryGetValue(stage, out var actions))
            middlewareActions[stage] = actions = [];

        actions.Add(action);
    }

    public void AddEndpoints(Action<IEndpointRouteBuilder> action) => endpointActions.Add(action);

    /// <summary>
    /// The credential schemes Spark's composite authenticate handler tries, in order. First success
    /// wins.
    /// <para>
    /// Spark's endpoints carry no <c>[Authorize]</c> or <c>RequireAuthorization</c> metadata — they
    /// are anonymous at the ASP.NET layer and decide inside the handler via
    /// <c>IPermissionService</c>. That means a scheme runs only if it is the <i>default
    /// authenticate scheme</i>; nothing else asks. Registering an extra scheme therefore had no
    /// effect at all on a Spark endpoint, and a caller presenting an unrecognised credential
    /// arrived as anonymous with <c>Everyone</c> rights, with no error and no log (F7).
    /// </para>
    /// </summary>
    public IReadOnlyList<SparkCredentialScheme> CredentialSchemes => credentialSchemes;

    /// <summary>
    /// Declares that <paramref name="scheme"/> participates in authenticating Spark requests.
    /// Registering the same scheme twice keeps the first declaration, so the order a caller sees is
    /// the order schemes were first added.
    /// </summary>
    public void AddCredentialScheme(string scheme, bool isAmbient = false)
    {
        if (credentialSchemes.Any(s => string.Equals(s.Name, scheme, StringComparison.Ordinal)))
            return;

        credentialSchemes.Add(new SparkCredentialScheme(scheme, isAmbient));
    }

    /// <summary>
    /// Runs everything registered for <paramref name="stage"/>, in registration order, and marks the
    /// stage applied so a later <see cref="AddMiddleware"/> for it fails loudly.
    /// <para>
    /// <paramref name="stage"/> is deliberately <b>not</b> optional. A default would let a caller
    /// apply one stage and silently drop the other — losing middleware with no error, which is
    /// exactly what the applied-stage guard exists to prevent. Every caller states which stage it is
    /// building, so <c>UseSpark()</c> cannot half-wire the pipeline by omission.
    /// </para>
    /// </summary>
    public void ApplyMiddleware(IApplicationBuilder app, SparkMiddlewareStage stage)
    {
        ArgumentNullException.ThrowIfNull(app);

        appliedStages.Add(stage);

        if (!middlewareActions.TryGetValue(stage, out var actions))
            return;

        foreach (var action in actions)
            action(app);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        foreach (var action in endpointActions)
            action(endpoints);
    }
}
