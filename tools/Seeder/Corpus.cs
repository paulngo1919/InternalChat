using System.Text;

namespace InternalChat.Seeder;

/// <summary>
/// Generates message bodies for seeded rows.
/// </summary>
/// <remarks>
/// <para>
/// Not decoration. research.md D9 accepts PostgreSQL full-text search for v1 <em>on condition</em>
/// that the search budget is measured against a full-retention corpus, and a corpus of 125 million
/// identical strings measures nothing: the GIN index would hold a handful of distinct lexemes and
/// answer every query instantly. The budget would pass and mean nothing.
/// </para>
/// <para>
/// So bodies are assembled from a vocabulary with a skewed distribution — a few very common words,
/// a long tail of rare ones — which is what gives the index a realistic shape. Both Vietnamese and
/// English words are included: the platform is bilingual in practice, and Vietnamese diacritics are
/// exactly what the <c>unaccent</c> configuration in T163 has to handle.
/// </para>
/// <para>
/// Generation is deterministic in the seed, so a search benchmark measured today can be repeated
/// against an identical corpus next month.
/// </para>
/// </remarks>
internal static class Corpus
{
    /// <summary>Frequent words — the head of the distribution.</summary>
    private static readonly string[] Common =
    [
        "đã", "xong", "chưa", "được", "nhé", "ạ", "team", "meeting", "deploy", "review",
        "ok", "cảm ơn", "sẽ", "làm", "cần", "gấp", "hôm nay", "ngày mai", "sprint", "ticket",
    ];

    /// <summary>Rarer words — the long tail that makes an index selective.</summary>
    private static readonly string[] Rare =
    [
        "migration", "rollback", "partition", "throughput", "quarantine", "webhook", "idempotent",
        "retention", "backlog", "khẩn", "biên bản", "nghiệm thu", "bàn giao", "hợp đồng", "báo cáo",
        "phân tích", "tối ưu", "sự cố", "khắc phục", "kiểm thử", "triển khai", "cấu hình",
        "latency", "outbox", "keyset", "trigram", "tsvector", "prefetch", "dead-letter", "confirm",
    ];

    /// <summary>Sentence frames, so bodies read like messages rather than word salad.</summary>
    private static readonly string[] Frames =
    [
        "{0} {1} {2}?",
        "{0} — {1} {2}.",
        "Mình {0} {1}, {2} nhé.",
        "Can you {1} the {2}? {0}.",
        "{1} {2}. {0}",
        "Update: {1} {2} ({0}).",
    ];

    /// <summary>
    /// Builds one message body from a seed. The same seed always yields the same body.
    /// </summary>
    public static string Sentence(int seed)
    {
        // xorshift32 rather than Random: no allocation, no shared state across the parallel COPY
        // writers the load seeder uses, and reproducible without carrying an instance around.
        uint state = unchecked((uint)seed * 2654435761u) | 1u;

        string frame = Frames[(int)(Next(ref state) % (uint)Frames.Length)];

        // Roughly four in five words come from the common set. A uniform draw over the whole
        // vocabulary would make every word equally selective, which no real corpus is.
        string a = Draw(ref state);
        string b = Draw(ref state);
        string c = Draw(ref state);

        StringBuilder body = new(string.Format(System.Globalization.CultureInfo.InvariantCulture, frame, a, b, c));

        // A minority of messages are long. Retrieval cost depends on body length distribution, and
        // a corpus where every row is eight words understates it.
        if (Next(ref state) % 10 == 0)
        {
            int extra = (int)(Next(ref state) % 40) + 10;
            for (int i = 0; i < extra; i++)
            {
                body.Append(' ').Append(Draw(ref state));
            }
        }

        return body.ToString();
    }

    private static string Draw(ref uint state) =>
        Next(ref state) % 5 == 0
            ? Rare[(int)(Next(ref state) % (uint)Rare.Length)]
            : Common[(int)(Next(ref state) % (uint)Common.Length)];

    private static uint Next(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }
}
