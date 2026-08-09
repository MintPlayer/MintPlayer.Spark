namespace MintPlayer.Spark.Abstractions.Builder;

public class SparkModuleRegistry
{
    public Type? IdentityUserType { get; set; }

    private readonly List<Action<IApplicationBuilder>> middlewareActions = [];
    private readonly List<Action<IEndpointRouteBuilder>> endpointActions = [];
    private readonly List<SparkCredentialScheme> credentialSchemes = [];

    public void AddMiddleware(Action<IApplicationBuilder> action) => middlewareActions.Add(action);
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

    public void ApplyMiddleware(IApplicationBuilder app)
    {
        foreach (var action in middlewareActions)
            action(app);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        foreach (var action in endpointActions)
            action(endpoints);
    }
}
