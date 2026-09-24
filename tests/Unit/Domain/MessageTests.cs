using System.Globalization;
using InternalChat.Domain.Common;
using InternalChat.Domain.Messages;

namespace InternalChat.UnitTests.Domain;

/// <summary>
/// T077 — message body length, the required ULID key, and the 24-hour edit and delete window.
/// </summary>
/// <remarks>
/// data-model.md's validation summary lists these as invariants that MUST be unit-tested. They are
/// enforced on the value objects and the entity rather than at an endpoint, so every path that ever
/// creates a message goes through the same gate instead of each new one having to remember.
/// </remarks>
public sealed class MessageTests : UnitTestBase
{
    private const string ValidKey = "01JBXQ7ZPT4M9WYFN2VKC3H6RD";

    private static Message Sent(IClock clock, string body = "hello", Guid? authorId = null) =>
        Message.Send(
            Guid.CreateVersion7(),
            conversationId: Guid.CreateVersion7(),
            seq: 1,
            authorId: authorId ?? Guid.CreateVersion7(),
            ClientMessageKey.Parse(ValidKey),
            MessageBody.Create(body),
            clock);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n  ")]
    public void A_whitespace_only_body_is_rejected(string body)
    {
        // FR-019. Whitespace-only is called out separately from empty because a client that trims
        // nothing will happily send a single space, and a conversation full of blank bubbles is a
        // defect nobody can explain afterwards.
        Assert.Throws<ArgumentException>(() => MessageBody.Create(body));
    }

    [Fact]
    public void A_body_at_the_limit_is_accepted_and_one_character_over_is_not()
    {
        string atLimit = new('a', MessageBody.MaximumLength);

        Assert.Equal(MessageBody.MaximumLength, MessageBody.Create(atLimit).Value.Length);
        Assert.Throws<ArgumentException>(() => MessageBody.Create(atLimit + "a"));
    }

    [Fact]
    public void A_body_is_trimmed_but_internal_whitespace_is_preserved()
    {
        MessageBody body = MessageBody.Create("  hello   world  ");

        // Trailing whitespace is noise; the space between words is content. Collapsing internal
        // whitespace would quietly rewrite code snippets and pasted tables.
        Assert.Equal("hello   world", body.Value);
    }

    [Fact]
    public void The_length_limit_is_measured_after_trimming()
    {
        // Otherwise a body of 8,000 characters with a trailing newline is refused for being 8,001
        // long, and the sender is told their message is too long when it is not.
        string padded = "  " + new string('a', MessageBody.MaximumLength) + "  ";

        Assert.Equal(MessageBody.MaximumLength, MessageBody.Create(padded).Value.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("too-short")]
    [InlineData("01JBXQ7ZPT4M9WYFN2VKC3H6RDEXTRA")]
    [InlineData("01JBXQ7ZPT4M9WYFN2VKC3H6RU")] // 'U' is not in Crockford base32.
    [InlineData("01JBXQ7ZPT4M9WYFN2VKC3H6RI")] // Nor is 'I'.
    public void A_client_message_key_that_is_not_a_ulid_is_rejected(string key)
    {
        // FR-011. The uniqueness of (conversation_id, client_message_key) IS the exactly-once
        // guarantee, so a malformed key is refused at the door rather than stored — a key the
        // client cannot reproduce on retry would silently duplicate the message.
        Assert.Throws<ArgumentException>(() => ClientMessageKey.Parse(key));
    }

    [Fact]
    public void A_client_message_key_is_compared_case_insensitively()
    {
        // Crockford base32 is case-insensitive by specification, and clients differ on which case
        // they emit. Two keys that differ only in case are the same key — treating them as
        // different would let a retry from a client that changed case produce a second message.
        Assert.Equal(ClientMessageKey.Parse(ValidKey), ClientMessageKey.Parse(ValidKey.ToLowerInvariant()));
    }

    [Fact]
    public void An_edit_inside_the_window_updates_the_body_and_records_when()
    {
        TestClock clock = new();
        Message message = Sent(clock);

        clock.Advance(TimeSpan.FromHours(23));
        message.Edit(message.AuthorId, MessageBody.Create("corrected"), clock);

        Assert.Equal("corrected", message.Body?.Value);
        Assert.Equal(clock.UtcNow, message.EditedAt);

        // Ordering must survive an edit: seq and sent_at are the authority, and moving either
        // would reorder the conversation for everyone reading it.
        Assert.Equal(1, message.Seq);
        Assert.Equal(DateTimeOffset.Parse("2026-08-01T09:00:00Z", CultureInfo.InvariantCulture), message.SentAt);
    }

    [Fact]
    public void An_edit_beyond_twenty_four_hours_is_refused()
    {
        TestClock clock = new();
        Message message = Sent(clock);

        clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));

        Assert.Throws<EditWindowExpiredException>(
            () => message.Edit(message.AuthorId, MessageBody.Create("too late"), clock));
    }

    [Fact]
    public void The_window_boundary_is_inclusive()
    {
        // Exactly 24 hours is inside. Stated as its own test because "within 24 hours" and
        // "less than 24 hours" differ by one second, and which one was meant is invisible in code
        // that uses `<` where it meant `<=`.
        TestClock clock = new();
        Message message = Sent(clock);

        clock.Advance(EditWindow.Duration);
        message.Edit(message.AuthorId, MessageBody.Create("just in time"), clock);

        Assert.Equal("just in time", message.Body?.Value);
    }

    [Fact]
    public void Only_the_author_may_edit()
    {
        TestClock clock = new();
        Message message = Sent(clock);

        // On the entity rather than in the use case, so a second edit path added later cannot
        // forget it.
        Assert.Throws<NotTheAuthorException>(
            () => message.Edit(Guid.CreateVersion7(), MessageBody.Create("not mine"), clock));
    }

    [Fact]
    public void Deleting_clears_the_body_and_leaves_a_tombstone()
    {
        TestClock clock = new();
        Message message = Sent(clock);

        message.Delete(message.AuthorId, clock);

        // The row survives so ordering, sequence continuity, and the audit trail stay intact
        // (data-model.md). The body does not — a deleted message must not be readable.
        Assert.Null(message.Body);
        Assert.Equal(clock.UtcNow, message.DeletedAt);
        Assert.Equal(1, message.Seq);
    }

    [Fact]
    public void A_deleted_message_cannot_be_edited_back_into_existence()
    {
        TestClock clock = new();
        Message message = Sent(clock);

        message.Delete(message.AuthorId, clock);

        // `deleted` is terminal (data-model.md state transitions). Without this, editing a
        // tombstone would restore content the author deliberately removed.
        Assert.Throws<MessageDeletedException>(
            () => message.Edit(message.AuthorId, MessageBody.Create("back"), clock));
    }

    [Fact]
    public void Deleting_twice_does_not_move_the_timestamp()
    {
        TestClock clock = new();
        Message message = Sent(clock);

        message.Delete(message.AuthorId, clock);
        DateTimeOffset first = message.DeletedAt!.Value;

        clock.Advance(TimeSpan.FromMinutes(5));
        message.Delete(message.AuthorId, clock);

        // Same reasoning as Employee.Deactivate: the timestamp is what an auditor reads to
        // establish when the content stopped being available, and a retry must not rewrite it.
        Assert.Equal(first, message.DeletedAt);
    }

    [Fact]
    public void Sending_raises_the_event_the_outbox_dispatches()
    {
        TestClock clock = new();
        Message message = Sent(clock);

        // Raised, not published: the transaction behavior drains these into the outbox so the row
        // and its event commit together (Principle VI).
        IDomainEvent raised = Assert.Single(message.DomainEvents);
        Assert.Equal("chat.message.sent.v1", raised.EventType);
    }

    [Fact]
    public void The_sent_event_carries_no_message_body()
    {
        TestClock clock = new();
        Message message = Sent(clock, body: "confidential salary figures");

        // FR-056 and contracts/messaging.md: bodies in queue payloads end up in broker logs, the
        // management UI, and DLQ dumps. Consumers that need the text read it from PostgreSQL.
        string serialized = System.Text.Json.JsonSerializer.Serialize(
            message.DomainEvents.Single(),
            message.DomainEvents.Single().GetType());

        Assert.DoesNotContain("confidential", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("salary", serialized, StringComparison.OrdinalIgnoreCase);
    }
}
