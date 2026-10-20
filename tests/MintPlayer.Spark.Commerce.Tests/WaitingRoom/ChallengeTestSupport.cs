using System.Text;
using System.Text.Json;
using MintPlayer.Spark.Challenges.Altcha;
using MintPlayer.Spark.Challenges.Contracts;

namespace MintPlayer.Spark.Commerce.Tests.WaitingRoom;

internal static class ChallengeTestSupport
{
    /// <summary>Solves an ALTCHA challenge the way the client does (brute force over 0..maxnumber), as the base64 JSON payload.</summary>
    public static string Solve(SparkAltchaChallenge challenge, string? signature = null, string? salt = null)
    {
        for (long n = 0; n <= challenge.Maxnumber; n++)
        {
            if (AltchaChallenge.Hash(challenge.Salt, n) != challenge.Challenge)
                continue;
            var payload = new Dictionary<string, object>
            {
                ["algorithm"] = challenge.Algorithm,
                ["challenge"] = challenge.Challenge,
                ["number"] = n,
                ["salt"] = salt ?? challenge.Salt,
                ["signature"] = signature ?? challenge.Signature,
            };
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
        }
        throw new InvalidOperationException("No solution within maxnumber.");
    }
}
