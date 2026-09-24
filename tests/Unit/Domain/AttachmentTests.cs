using InternalChat.Domain.Attachments;
using InternalChat.Domain.Common;

namespace InternalChat.UnitTests.Domain;

/// <summary>
/// T148 — the scan state machine and the retrievability rule it gates (FR-024, FR-025, FR-027).
/// </summary>
/// <remarks>
/// The transitions come from data-model.md: <c>pending → clean | infected | failed</c> and
/// <c>failed → pending</c> on retry, with <c>clean</c> and <c>infected</c> terminal. They are
/// tested here rather than only through the scan consumer because the consumer is one caller of
/// several — the retry job and the retention sweep are others — and a state machine enforced by
/// its callers is not enforced.
/// </remarks>
public sealed class AttachmentTests : UnitTestBase
{
    private static readonly Guid ConversationId = Guid.CreateVersion7();
    private static readonly Guid UploaderId = Guid.CreateVersion7();

    private static Attachment Reserved(
        IClock clock,
        AttachmentKind kind = AttachmentKind.Image,
        string contentType = "image/png",
        long byteSize = 4096,
        int? durationSeconds = null,
        string fileName = "screenshot.png") =>
        Attachment.Reserve(
            Guid.CreateVersion7(),
            ConversationId,
            UploaderId,
            kind,
            contentType,
            byteSize,
            durationSeconds,
            fileName,
            clock);

    [Fact]
    public void A_reserved_attachment_starts_pending_and_is_not_retrievable()
    {
        TestClock clock = new();
        Attachment attachment = Reserved(clock);

        Assert.Equal(ScanVerdict.Pending, attachment.ScanStatus);
        Assert.Null(attachment.ScannedAt);
        Assert.Null(attachment.MessageId);

        // FR-024: nothing becomes retrievable before it has been scanned. The default state is the
        // one that holds if every later step fails to run.
        Assert.False(attachment.IsRetrievable(messageIsDeleted: false));
    }

    [Fact]
    public void Reservation_normalizes_the_content_type_and_derives_the_object_key()
    {
        TestClock clock = new();
        Attachment attachment = Reserved(clock, contentType: "IMAGE/PNG; charset=binary");

        Assert.Equal("image/png", attachment.ContentType);
        Assert.Equal($"{ConversationId}/{attachment.Id}", attachment.ObjectKey);
    }

    [Fact]
    public void Reservation_enforces_the_file_constraints()
    {
        TestClock clock = new();

        Assert.Throws<UnsupportedContentTypeException>(() =>
            Reserved(clock, contentType: "image/svg+xml"));

        Assert.Throws<FileTooLargeException>(() =>
            Reserved(clock, byteSize: FileConstraints.MaximumImageBytes + 1));

        Assert.Throws<VideoTooLongException>(() =>
            Reserved(clock, AttachmentKind.Video, "video/mp4", 4096, durationSeconds: 601));
    }

    [Fact]
    public void A_duration_is_kept_for_a_video_and_dropped_for_an_image()
    {
        TestClock clock = new();

        Assert.Equal(90, Reserved(clock, AttachmentKind.Video, "video/mp4", 4096, 90, "clip.mp4").DurationSeconds);

        // Validate tolerates a stray duration on an image; persisting it would put a meaningless
        // number in a column whose CHECK constraint exists for videos.
        Assert.Null(Reserved(clock, durationSeconds: 42).DurationSeconds);
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\Users\\me\\holiday.png", "holiday.png")]
    [InlineData("  spaced.png  ", "spaced.png")]
    public void A_file_name_is_reduced_to_its_leaf(string given, string expected)
    {
        TestClock clock = new();

        // The name never becomes a path — ObjectKey is built from identifiers — but it is echoed
        // into a Content-Disposition header, where separators have no business.
        Assert.Equal(expected, Reserved(clock, fileName: given).FileName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    public void A_missing_file_name_is_refused(string fileName)
    {
        TestClock clock = new();
        Assert.Throws<ArgumentException>(() => Reserved(clock, fileName: fileName));
    }

    [Fact]
    public void A_long_file_name_is_truncated_rather_than_refused()
    {
        TestClock clock = new();
        string tooLong = new string('a', 400) + ".png";

        // Refusing would fail an upload over display metadata. The column is bounded; the name is
        // not load-bearing.
        Assert.Equal(Attachment.MaximumFileNameLength, Reserved(clock, fileName: tooLong).FileName.Length);
    }

    [Fact]
    public void A_clean_verdict_makes_it_retrievable_and_records_the_measured_size()
    {
        TestClock clock = new();
        Attachment attachment = Reserved(clock, byteSize: 4096);

        clock.Advance(TimeSpan.FromSeconds(30));
        attachment.MarkClean(confirmedByteSize: 4000, posterObjectKey: null, clock);

        Assert.Equal(ScanVerdict.Clean, attachment.ScanStatus);
        Assert.Equal(clock.UtcNow, attachment.ScannedAt);

        // The declared size got it past the gate; the measured one is what the proxy will serve.
        Assert.Equal(4000, attachment.ByteSize);
        Assert.True(attachment.IsRetrievable(messageIsDeleted: false));

        AttachmentScanned scanned = Assert.Single(attachment.DomainEvents.OfType<AttachmentScanned>());
        Assert.Equal(attachment.Id, scanned.AttachmentId);
        Assert.Equal(ConversationId, scanned.ConversationId);
        Assert.Equal(ScanVerdict.Clean, scanned.Verdict);

        // The contract's name, not an invented one. contracts/messaging.md declares a single
        // chat.attachment.scanned.v1 carrying the verdict; AttachmentReady is the SignalR event.
        Assert.Equal("chat.attachment.scanned.v1", scanned.EventType);
    }

    [Fact]
    public void A_clean_video_carries_its_poster_key()
    {
        TestClock clock = new();
        Attachment attachment = Reserved(clock, AttachmentKind.Video, "video/mp4", 4096, 90, "clip.mp4");

        attachment.MarkClean(4096, posterObjectKey: $"{ConversationId}/poster", clock);

        Assert.Equal($"{ConversationId}/poster", attachment.PosterObjectKey);
    }

    [Fact]
    public void An_infected_verdict_leaves_it_unretrievable_and_names_the_file_to_its_uploader()
    {
        TestClock clock = new();
        Attachment attachment = Reserved(clock, fileName: "invoice.png");

        attachment.MarkInfected(clock);

        Assert.Equal(ScanVerdict.Infected, attachment.ScanStatus);
        Assert.False(attachment.IsRetrievable(messageIsDeleted: false));

        AttachmentScanned scanned = Assert.Single(attachment.DomainEvents.OfType<AttachmentScanned>());
        Assert.Equal(ScanVerdict.Infected, scanned.Verdict);

        // No file name on the wire: it is user-supplied text, and a queue payload carrying it would
        // put it in broker logs and DLQ dumps (FR-056). The uploader is told which file was
        // rejected through the notification path, which reads the row.
        Assert.Equal("chat.attachment.scanned.v1", scanned.EventType);
    }

    [Fact]
    public void A_failed_scan_is_neither_retrievable_nor_terminal()
    {
        TestClock clock = new();
        Attachment attachment = Reserved(clock);

        attachment.MarkScanFailed(clock);

        Assert.Equal(ScanVerdict.Failed, attachment.ScanStatus);
        Assert.False(attachment.IsRetrievable(messageIsDeleted: false));

        // Still emits a verdict. The contract declares one event for all three outcomes, and a
        // silent failure would leave a client showing "scanning…" forever with nothing to retry.
        Assert.Equal(
            ScanVerdict.Failed,
            Assert.Single(attachment.DomainEvents.OfType<AttachmentScanned>()).Verdict);

        attachment.RetryScan();

        Assert.Equal(ScanVerdict.Pending, attachment.ScanStatus);
        Assert.Null(attachment.ScannedAt);
    }

    [Fact]
    public void A_clean_verdict_cannot_be_overwritten()
    {
        TestClock clock = new();
        Attachment attachment = Reserved(clock);
        attachment.MarkClean(4096, null, clock);

        Assert.Throws<ScanVerdictConflictException>(() => attachment.MarkInfected(clock));
        Assert.Throws<ScanVerdictConflictException>(() => attachment.MarkScanFailed(clock));
        Assert.Throws<ScanVerdictConflictException>(() => attachment.MarkClean(4096, null, clock));
        Assert.Throws<ScanVerdictConflictException>(attachment.RetryScan);
    }

    [Fact]
    public void An_infected_verdict_cannot_be_laundered_into_a_clean_one()
    {
        TestClock clock = new();
        Attachment attachment = Reserved(clock);
        attachment.MarkInfected(clock);

        // The reason retry is restricted to `failed`: if it accepted `infected`, re-running the
        // scanner against a definition set that no longer flags the file would make it retrievable.
        Assert.Throws<ScanVerdictConflictException>(attachment.RetryScan);
        Assert.Throws<ScanVerdictConflictException>(() => attachment.MarkClean(4096, null, clock));
    }

    [Fact]
    public void A_deleted_message_stops_a_clean_attachment_being_served()
    {
        TestClock clock = new();
        Attachment attachment = Reserved(clock);
        attachment.MarkClean(4096, null, clock);

        // FR-027. The attachment's own state is untouched — the row and the object survive for the
        // retention sweep — but it stops being served the moment its message becomes a tombstone.
        Assert.False(attachment.IsRetrievable(messageIsDeleted: true));
        Assert.Equal(ScanVerdict.Clean, attachment.ScanStatus);
    }

    [Fact]
    public void Attaching_to_a_message_is_write_once_but_idempotent()
    {
        TestClock clock = new();
        Attachment attachment = Reserved(clock);
        Guid messageId = Guid.CreateVersion7();
        DateTimeOffset sentAt = clock.UtcNow;

        attachment.AttachTo(messageId, sentAt);
        Assert.Equal(messageId, attachment.MessageId);

        // Binding is what triggers the scan: the client PUTs bytes straight to MinIO, so a send
        // naming the attachment is the first thing that tells the server the object exists.
        AttachmentUploaded uploaded = Assert.Single(attachment.DomainEvents.OfType<AttachmentUploaded>());
        Assert.Equal(attachment.ObjectKey, uploaded.ObjectKey);
        Assert.Equal("chat.attachment.uploaded.v1", uploaded.EventType);

        // The partition key travels with the id. Without it the download path cannot read the
        // message it must check for deletion (FR-027) without scanning every monthly partition.
        Assert.Equal(sentAt, attachment.MessageSentAt);

        // A retried send carrying the same attachment must not fail (FR-011 idempotency).
        attachment.AttachTo(messageId, sentAt);
        Assert.Equal(messageId, attachment.MessageId);

        // Re-pointing would move a file between conversations without ConversationId — the
        // authorization subject — changing with it, so no membership check would notice.
        Assert.Throws<InvalidOperationException>(() =>
            attachment.AttachTo(Guid.CreateVersion7(), sentAt));
    }

    [Fact]
    public void A_non_positive_confirmed_size_is_refused()
    {
        TestClock clock = new();
        Attachment attachment = Reserved(clock);

        // An empty object in quarantine means an interrupted upload, not a zero-byte file worth
        // promoting (FR-026).
        Assert.Throws<ArgumentOutOfRangeException>(() => attachment.MarkClean(0, null, clock));
    }
}
