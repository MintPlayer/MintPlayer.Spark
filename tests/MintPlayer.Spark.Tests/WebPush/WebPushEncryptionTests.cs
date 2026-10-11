using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using MintPlayer.Spark.WebPush;

namespace MintPlayer.Spark.Tests.WebPush;

/// <summary>
/// RFC 8291 message encryption (<c>docs/seo_analyzer_PRD.md</c> D38), checked byte for byte against the
/// RFC's own example (§5) and its intermediate values (Appendix A), then round-tripped through a receiver
/// written from the RFC's decryption steps.
/// </summary>
public class WebPushEncryptionTests
{
    // RFC 8291 §5 and Appendix A, copied from https://www.rfc-editor.org/rfc/rfc8291.txt (whitespace removed).
    private const string Plaintext = "When I grow up, I want to be a watermelon";
    private const string AsPublic = "BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8";
    private const string AsPrivate = "yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw";
    private const string UaPublic = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";
    private const string UaPrivate = "q1dXpw3UpT5VOmu_cf_v6ih07Aems3njxI-JWgLcM94";
    private const string Salt = "DGv6ra1nlYgDCS1FRnbzlw";
    private const string AuthSecret = "BTBZMqHH6r4Tts7J_aSIgg";
    private const string Header = "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8";
    private const string Ciphertext = "8pfeW0KbunFT06SuDKoJH9Ql87S1QUrdirN6GcG7sFz1y1sqLgVi1VhjVkHsUoEsbI_0LpXMuGvnzQ";
    private const string Body = "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN";
    private const string EcdhSecret = "kyrL1jIIOHEzg3sM2ZWRHDRB62YACZhhSlknJ672kSs";
    private const string Ikm = "S4lYMb_L0FxCeq0WhDx813KgSYqU26kOyzWUdsXYyrg";
    private const string Prk = "09_eUZGrsvxChDCGRCdkLiDXrReGOEVeSCdCcPBSJSc";
    private const string Cek = "oIhVW04MRdy2XN9CiKLxTg";
    private const string Nonce = "4h_95klXJ5E_qnoN";

    private static WebPushSubscription RfcSubscription => new("https://push.example.net/push/JzLQ3raZJfFBR0aqvOMsLrt54w4rJUsV", UaPublic, AuthSecret);

    [Fact]
    public void The_RFC_8291_example_encrypts_to_exactly_the_RFC_body()
    {
        using var applicationServer = KeyPair(AsPublic, AsPrivate);

        var body = WebPushEncryption.Encrypt(Encoding.ASCII.GetBytes(Plaintext), RfcSubscription, Base64Url.DecodeFromChars(Salt), applicationServer);

        Base64Url.EncodeToString(body).Should().Be(Body);
        Base64Url.EncodeToString(body.AsSpan(0, WebPushEncryption.HeaderLength)).Should().Be(Header);
        Base64Url.EncodeToString(body.AsSpan(WebPushEncryption.HeaderLength)).Should().Be(Ciphertext);
        // 86 header + 41 plaintext + 1 delimiter + 16 tag. (The RFC §5 request line says Content-Length: 145; its
        // body, which this matches byte for byte, decodes to 144.)
        body.Length.Should().Be(144);
    }

    [Fact]
    public void The_receiver_side_of_the_RFC_example_reproduces_every_intermediate_value()
    {
        // Appendix A computed from the user agent's private key: the derivation the encryptor runs from the
        // other side, so a symmetric mistake (both sides wrong the same way) cannot pass.
        using var userAgent = KeyPair(UaPublic, UaPrivate);
        var steps = Receiver.Derive(Base64Url.DecodeFromChars(Body), userAgent, Base64Url.DecodeFromChars(AuthSecret));

        Base64Url.EncodeToString(steps.EcdhSecret).Should().Be(EcdhSecret);
        Base64Url.EncodeToString(steps.Ikm).Should().Be(Ikm);
        Base64Url.EncodeToString(steps.Prk).Should().Be(Prk);
        Base64Url.EncodeToString(steps.Cek).Should().Be(Cek);
        Base64Url.EncodeToString(steps.Nonce).Should().Be(Nonce);
        Encoding.ASCII.GetString(steps.Plaintext).Should().Be(Plaintext);
    }

    [Fact]
    public void A_fresh_salt_and_key_per_message_and_the_receiver_reads_it_back()
    {
        using var userAgent = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var auth = RandomNumberGenerator.GetBytes(16);
        var subscription = new WebPushSubscription("https://push.example.net/x",
            Base64Url.EncodeToString(Uncompressed(userAgent)), Base64Url.EncodeToString(auth));
        var payload = Encoding.UTF8.GetBytes("{\"notification\":{\"title\":\"Score dropped\",\"body\":\"é ✓\"}}");

        var first = WebPushEncryption.Encrypt(payload, subscription);
        var second = WebPushEncryption.Encrypt(payload, subscription);

        first.AsSpan(0, 16).SequenceEqual(second.AsSpan(0, 16)).Should().BeFalse();        // salt
        first.AsSpan(21, 65).SequenceEqual(second.AsSpan(21, 65)).Should().BeFalse();      // ephemeral key
        BinaryPrimitives.ReadUInt32BigEndian(first.AsSpan(16, 4)).Should().Be(4096u);
        first[20].Should().Be((byte)65);
        Receiver.Derive(first, userAgent, auth).Plaintext.SequenceEqual(payload).Should().BeTrue();
        Receiver.Derive(second, userAgent, auth).Plaintext.SequenceEqual(payload).Should().BeTrue();
    }

    [Fact]
    public void The_largest_payload_fits_4096_bytes_and_one_more_byte_is_refused()
    {
        using var userAgent = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var subscription = new WebPushSubscription("https://push.example.net/x",
            Base64Url.EncodeToString(Uncompressed(userAgent)), Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16)));

        WebPushMessage.MaxPayloadBytes.Should().Be(3993);
        WebPushEncryption.Encrypt(new byte[3993], subscription).Length.Should().Be(4096);
        var act = () => WebPushEncryption.Encrypt(new byte[3994], subscription);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("not base64!", AuthSecret)]
    [InlineData("BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcx", AuthSecret)]                     // 33 bytes
    [InlineData(UaPublic, "BTBZMqHH6r4Tts7J")]                                                        // 12-byte auth
    [InlineData("AyVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4", AuthSecret)] // not 0x04
    [InlineData("BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAE", AuthSecret)] // off the curve
    public void Malformed_subscription_keys_are_refused_as_arguments(string p256dh, string auth)
    {
        var act = () => WebPushEncryption.Encrypt("x"u8, new WebPushSubscription("https://push.example.net/x", p256dh, auth));
        act.Should().Throw<ArgumentException>();
    }

    internal static ECDiffieHellman KeyPair(string publicKey, string privateKey)
    {
        var q = Base64Url.DecodeFromChars(publicKey);
        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = q[1..33], Y = q[33..65] },
            D = Base64Url.DecodeFromChars(privateKey),
        });
    }

    internal static byte[] Uncompressed(ECAlgorithm key)
    {
        var q = key.ExportParameters(false).Q;
        return [0x04, .. q.X!, .. q.Y!];
    }

    /// <summary>The user agent's half of RFC 8291 §3 / RFC 8188 §2, written independently of the library.</summary>
    internal static class Receiver
    {
        public sealed record Steps(byte[] EcdhSecret, byte[] Ikm, byte[] Prk, byte[] Cek, byte[] Nonce, byte[] Plaintext);

        public static Steps Derive(byte[] body, ECDiffieHellman userAgent, byte[] authSecret)
        {
            var salt = body[..16];
            var keyIdLength = body[20];
            var serverPublic = body[21..(21 + keyIdLength)];
            var record = body[(21 + keyIdLength)..];

            using var server = ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = serverPublic[1..33], Y = serverPublic[33..65] },
            });
            var ecdh = userAgent.DeriveRawSecretAgreement(server.PublicKey);

            // PRK_key = HMAC(auth_secret, ecdh_secret); IKM = HMAC(PRK_key, key_info ‖ 0x01)
            var prkKey = HMACSHA256.HashData(authSecret, ecdh);
            byte[] keyInfo = [.. "WebPush: info\0"u8, .. Uncompressed(userAgent), .. serverPublic];
            var ikm = HMACSHA256.HashData(prkKey, (byte[])[.. keyInfo, 0x01]);

            // PRK = HMAC(salt, IKM); CEK = HMAC(PRK, cek_info ‖ 0x01)[..16]; NONCE = HMAC(PRK, nonce_info ‖ 0x01)[..12]
            var prk = HMACSHA256.HashData(salt, ikm);
            byte[] cekInfo = [.. "Content-Encoding: aes128gcm\0"u8, 0x01];
            byte[] nonceInfo = [.. "Content-Encoding: nonce\0"u8, 0x01];
            var cek = HMACSHA256.HashData(prk, cekInfo)[..16];
            var nonce = HMACSHA256.HashData(prk, nonceInfo)[..12];

            var padded = new byte[record.Length - 16];
            using (var aes = new AesGcm(cek, 16))
                aes.Decrypt(nonce, record[..^16], record[^16..], padded);

            // Strip the padding: trailing zeros, then the delimiter, which must be 0x02 for the last record.
            var end = padded.Length - 1;
            while (end >= 0 && padded[end] == 0)
                end--;
            padded[end].Should().Be((byte)0x02);
            return new Steps(ecdh, ikm, prk, cek, nonce, padded[..end]);
        }
    }
}
