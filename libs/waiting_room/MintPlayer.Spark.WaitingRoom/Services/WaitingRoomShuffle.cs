using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MintPlayer.Spark.WaitingRoom.Services;

/// <summary>
/// The shuffle of the randomized pre-queue and the ballot draw, <c>spark-shuffle-v1</c>, deterministic and publicly
/// checkable (§4.12 "provably fair"):
/// <list type="number">
/// <item>The seed (32 random bytes) is created with the room's state and its SHA-256 published as the commitment before
/// joining closes.</item>
/// <item>At the start the entry ids are sorted ordinally; <c>inputHash = SHA-256(ids joined by '\n')</c>.</item>
/// <item>A stream of 64-bit values comes from <c>HMAC-SHA256(key = seed ‖ inputHash, message = counter as 8 bytes
/// little-endian)</c>, four values per block.</item>
/// <item>Fisher–Yates from the end, with rejection sampling so every position is equally likely (no modulo bias).</item>
/// </list>
/// The seed is revealed after the shuffle, with the sorted ids (<c>GET …/draw</c>). Binding the input hash into the key
/// means the order depends on exactly the published set of entries.
/// </summary>
/// <remarks>
/// What this does not prove: the operator knows the seed before the start, so in principle it could add entries of its
/// own. The published id list and count make that visible after the fact; it does not prevent it (documented in
/// <c>docs/guide-waiting-room.md</c>).
/// </remarks>
public static class WaitingRoomShuffle
{
    public const string Algorithm = "spark-shuffle-v1";

    /// <summary>A new random seed (hex).</summary>
    public static string NewSeed() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>The commitment to a seed: hex SHA-256 of its bytes.</summary>
    public static string Commit(string seedHex) => Convert.ToHexStringLower(SHA256.HashData(Convert.FromHexString(seedHex)));

    /// <summary>The input hash over ids already sorted ordinally.</summary>
    public static string InputHash(IReadOnlyList<string> sortedIds)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', sortedIds))));

    /// <summary>The ids in drawn order (index 0 gets rank 1). Sorts a copy first, so callers may pass any order.</summary>
    public static IReadOnlyList<string> Order(IEnumerable<string> ids, string seedHex, out string inputHash)
    {
        var sorted = ids.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        inputHash = InputHash(sorted);
        var key = Convert.FromHexString(seedHex).Concat(Convert.FromHexString(inputHash)).ToArray();
        var random = new Stream64(key);
        for (var i = sorted.Length - 1; i > 0; i--)
        {
            var j = (int)random.Below((ulong)(i + 1));
            (sorted[i], sorted[j]) = (sorted[j], sorted[i]);
        }
        return sorted;
    }

    private sealed class Stream64(byte[] key)
    {
        private readonly HMACSHA256 hmac = new(key);
        private readonly byte[] block = new byte[32];
        private ulong counter;
        private int offset = 32;

        public ulong Next()
        {
            if (offset == 32)
            {
                Span<byte> message = stackalloc byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(message, counter++);
                hmac.TryComputeHash(message, block, out _);
                offset = 0;
            }
            var value = BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(offset, 8));
            offset += 8;
            return value;
        }

        /// <summary>Uniform in [0, bound).</summary>
        public ulong Below(ulong bound)
        {
            var limit = ulong.MaxValue - ulong.MaxValue % bound;
            ulong value;
            do value = Next(); while (value >= limit);
            return value % bound;
        }
    }
}
