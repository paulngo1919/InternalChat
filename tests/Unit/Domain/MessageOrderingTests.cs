using InternalChat.Domain.Common;
using InternalChat.Domain.Conversations;
using InternalChat.Domain.Messages;

namespace InternalChat.UnitTests.Domain;

/// <summary>
/// T078 — FR-012: ordering is derived from the server sequence and never from a client clock.
/// </summary>
/// <remarks>
/// <para>
/// research.md D1 rejects timestamp ordering outright: "Device clock skew reorders messages, and
/// FR-012 exists precisely to forbid this." quickstart V2 step 4 is the same scenario as a manual
/// check — set a device clock forward an hour and send; ordering is unaffected.
/// </para>
/// <para>
/// The thing being pinned here is that <see cref="Message"/> offers no way to supply a timestamp.
/// A test can only assert against the API a type exposes, so the strongest available statement is
/// that the sent-at value always comes from the injected clock and the ordering key always comes
/// from the caller-allocated sequence — and that a later message with an earlier wall-clock time
/// still sorts by sequence.
/// </para>
/// </remarks>
public sealed class MessageOrderingTests : UnitTestBase
{
    private static Message Sent(long seq, IClock clock, Guid conversationId) =>
        Message.Send(
            Guid.CreateVersion7(),
            conversationId,
            seq,
            authorId: Guid.CreateVersion7(),
            ClientMessageKey.New(),
            MessageBody.Create($"message {seq}"),
            clock);

    [Fact]
    public void Sent_at_comes_from_the_injected_clock()
    {
        TestClock clock = new("2026-08-01T09:00:00Z");

        Message message = Sent(1, clock, Guid.CreateVersion7());

        // The server's clock, never the sender's. There is deliberately no overload accepting a
        // timestamp — a client-supplied one is not an input this system has.
        Assert.Equal(clock.UtcNow, message.SentAt);
    }

    [Fact]
    public void Ordering_follows_the_sequence_even_when_wall_clock_time_runs_backwards()
    {
        Guid conversationId = Guid.CreateVersion7();

        // The pathological case: the second message is created when the clock reads an hour
        // EARLIER than the first. Under timestamp ordering it would sort first.
        Message first = Sent(1, new TestClock("2026-08-01T10:00:00Z"), conversationId);
        Message second = Sent(2, new TestClock("2026-08-01T09:00:00Z"), conversationId);

        Message[] ordered = [.. new[] { second, first }.OrderBy(m => m.Seq)];

        Assert.Equal(first.Id, ordered[0].Id);
        Assert.Equal(second.Id, ordered[1].Id);

        // And the timestamps genuinely do disagree, so the assertion above is not passing by
        // accident on two identical clocks.
        Assert.True(second.SentAt < first.SentAt);
    }

    [Fact]
    public void A_conversation_allocates_gapless_ascending_sequences()
    {
        Conversation conversation = Conversation.CreateGroup(
            Guid.CreateVersion7(),
            "Engineering",
            createdBy: Guid.CreateVersion7(),
            HistoryVisibility.FromJoin,
            new TestClock());

        long[] allocated = [.. Enumerable.Range(0, 5).Select(_ => conversation.AllocateSequence())];

        // Gapless, per research.md D1: "unread since seq N" and "deliver everything after seq N on
        // reconnect" are exact with gapless sequences and guesses without them.
        Assert.Equal([1, 2, 3, 4, 5], allocated);
        Assert.Equal(5, conversation.LastSeq);
    }

    [Fact]
    public void A_new_conversation_starts_at_sequence_zero()
    {
        Conversation conversation = Conversation.CreateGroup(
            Guid.CreateVersion7(),
            "Product Launch",
            createdBy: Guid.CreateVersion7(),
            HistoryVisibility.FromJoin,
            new TestClock());

        // Zero, not one. A member joining an empty conversation gets a history floor of 0, and the
        // first message is seq 1 — so `seq > visible_from_seq` includes it, which it must.
        Assert.Equal(0, conversation.LastSeq);
    }

    [Fact]
    public void An_edit_does_not_change_the_ordering_key()
    {
        TestClock clock = new("2026-08-01T09:00:00Z");
        Guid conversationId = Guid.CreateVersion7();

        Message message = Sent(7, clock, conversationId);
        DateTimeOffset originalSentAt = message.SentAt;

        clock.Advance(TimeSpan.FromHours(2));
        message.Edit(message.AuthorId, MessageBody.Create("corrected"), clock);

        // If an edit moved sent_at, the message would also move between monthly partitions — and
        // the partition key is part of the primary key.
        Assert.Equal(7, message.Seq);
        Assert.Equal(originalSentAt, message.SentAt);
    }

    [Fact]
    public void A_generated_client_message_key_is_a_well_formed_ulid()
    {
        // The client supplies this in production. The generator exists for tests and the seeder,
        // and must produce something the parser accepts — otherwise every test using it would be
        // exercising a key shape no real client sends.
        ClientMessageKey key = ClientMessageKey.New();

        Assert.Equal(key, ClientMessageKey.Parse(key.Value));
    }
}
