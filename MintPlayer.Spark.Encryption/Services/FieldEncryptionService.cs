using System.Security.Cryptography;

namespace MintPlayer.Spark.Encryption.Services;

/// <summary>
/// Provides AES-256-GCM encryption and decryption for individual field values.
/// Ciphertext format: <c>ENC:v1:{iv}:{ciphertext}:{tag}</c> (all segments Base64-encoded).
/// </summary>
internal sealed class FieldEncryptionService
{
    private const string Prefix = "ENC:v1:";
    private const int NonceSize = 12; // 96 bits for AES-GCM
    private const int TagSize = 16;   // 128-bit authentication tag

    /// <summary>
    /// Encrypts a plaintext string using the given Base64-encoded AES-256 key.
    /// Returns the ciphertext in <c>ENC:v1:{iv}:{ciphertext}:{tag}</c> format.
    /// </summary>
    public string Encrypt(string plaintext, string base64Key)
    {
        var key = Convert.FromBase64String(base64Key);
        var plaintextBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);

        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        return $"{Prefix}{Convert.ToBase64String(nonce)}:{Convert.ToBase64String(ciphertext)}:{Convert.ToBase64String(tag)}";
    }

    /// <summary>
    /// Decrypts an <c>ENC:v1:...</c> ciphertext string using the given Base64-encoded AES-256 key.
    /// Returns the original plaintext.
    /// </summary>
    public string Decrypt(string encryptedValue, string base64Key)
    {
        var key = Convert.FromBase64String(base64Key);

        // Strip prefix "ENC:v1:" and split into nonce:ciphertext:tag
        var payload = encryptedValue[Prefix.Length..];
        var parts = payload.Split(':');
        if (parts.Length != 3)
            throw new FormatException($"Invalid encrypted field format. Expected 3 segments after prefix, got {parts.Length}.");

        var nonce = Convert.FromBase64String(parts[0]);
        var ciphertext = Convert.FromBase64String(parts[1]);
        var tag = Convert.FromBase64String(parts[2]);

        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return System.Text.Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>
    /// Returns <c>true</c> if the value starts with the encryption prefix.
    /// </summary>
    public static bool IsEncrypted(string? value) => value != null && value.StartsWith(Prefix, StringComparison.Ordinal);
}
