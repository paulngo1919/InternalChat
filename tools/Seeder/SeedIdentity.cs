using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace InternalChat.Seeder;

/// <summary>
/// Deterministic identifiers for seeded rows.
/// </summary>
/// <remarks>
/// Every seeded id is derived from a name rather than generated randomly, which is what makes the
/// seeder safe to re-run: a second run computes the same ids, every insert hits
/// <c>ON CONFLICT DO NOTHING</c>, and the dataset does not double. Random ids would make
/// <c>docker compose up</c> twice produce forty employees and two copies of every conversation.
/// </remarks>
internal static class SeedIdentity
{
    /// <summary>
    /// Derives a stable UUID from a namespace and a key.
    /// </summary>
    public static Guid Id(string @namespace, string key)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"internalchat-seed:{@namespace}:{key}"));

        // Version 8 (RFC 9562, custom): says plainly that this id is derived, not random, so
        // nobody reads a collision into two rows sharing a prefix.
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>
    /// Builds the canonical <c>direct_key</c> for a pair — the sorted pair of employee ids.
    /// </summary>
    /// <remarks>
    /// Sorting is the whole mechanism (data-model.md, <c>conversation.direct_key</c> UNIQUE). It is
    /// what makes two people opening a direct conversation with each other at the same moment
    /// produce one row instead of two, and the seeder must build the key exactly as
    /// <c>CreateConversation</c> will in T091 or it will seed a duplicate the application then
    /// cannot use.
    /// </remarks>
    public static string DirectKey(Guid first, Guid second)
    {
        (Guid low, Guid high) = string.CompareOrdinal(first.ToString(), second.ToString()) <= 0
            ? (first, second)
            : (second, first);

        return $"{low}:{high}";
    }

    /// <summary>
    /// Produces a 26-character Crockford base32 string in the shape of a ULID.
    /// </summary>
    /// <remarks>
    /// The real value comes from the browser (research.md D1) and is the sender's idempotency key.
    /// Seeded rows still need one because <c>client_message_key</c> is NOT NULL and unique per
    /// conversation — this is a well-formed stand-in, derived from the same inputs as the row's id
    /// so a re-run collides on purpose.
    /// </remarks>
    public static string ClientMessageKey(Guid conversationId, long seq)
    {
        const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        byte[] hash = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{conversationId}:{seq.ToString(CultureInfo.InvariantCulture)}"));

        return string.Create(26, hash, static (span, source) =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                span[i] = Alphabet[source[i] & 0x1F];
            }
        });
    }
}
