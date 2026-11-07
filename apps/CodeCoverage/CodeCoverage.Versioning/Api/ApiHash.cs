using System.Security.Cryptography;
using System.Text;

namespace CodeCoverage.Versioning.Api;

/// <summary>
/// The form of an API hash (PRD D6, #576 D2): <c>sha256:&lt;64 hex&gt;</c> for a kind's first listing, and
/// <c>v&lt;N&gt;:sha256:&lt;64 hex&gt;</c> once that kind's listing has changed N-1 times. The hash names the listing
/// that produced it, so a hash travels through the manifest, the server and the tag annotation unchanged, and two hashes
/// made by different listings are never compared: the action then hashes the last release again from the registry.
/// </summary>
public static class ApiHash
{
	/// <summary>The hash of a listing's lines, marked with the listing's version.</summary>
	public static string Of(IEnumerable<string> lines, int listing)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(listing, 1);
		var text = string.Join("\n", lines) + "\n";
		var hash = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
		return listing == 1 ? hash : $"v{listing}:{hash}";
	}

	/// <summary>The listing version a well-formed hash was made by; a hash without a <c>v&lt;N&gt;:</c> prefix is 1.</summary>
	public static int Listing(string hash)
		=> hash.StartsWith('v') && hash.IndexOf(':') is > 1 and var colon && int.TryParse(hash.AsSpan(1, colon - 1), out var version) ? version : 1;

	/// <summary>Both hashes exist and were made by the same listing, so equal hashes mean an unchanged API.</summary>
	public static bool Comparable(string? a, string? b) => a is not null && b is not null && Listing(a) == Listing(b);
}
