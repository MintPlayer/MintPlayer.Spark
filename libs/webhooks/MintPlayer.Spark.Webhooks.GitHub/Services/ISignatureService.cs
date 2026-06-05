namespace MintPlayer.Spark.Webhooks.GitHub.Services;

public interface ISignatureService
{
    bool VerifySignature(string? signature, string secret, string requestBody);
}
