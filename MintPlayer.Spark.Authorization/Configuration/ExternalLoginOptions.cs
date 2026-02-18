namespace MintPlayer.Spark.Authorization.Configuration;

public class ExternalLoginOptions
{
    public ExternalProviderOptions? Google { get; set; }
    public ExternalProviderOptions? Microsoft { get; set; }
    public ExternalProviderOptions? Facebook { get; set; }
    public ExternalProviderOptions? Twitter { get; set; }
}

public class ExternalProviderOptions
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}
