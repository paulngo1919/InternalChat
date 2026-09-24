using InternalChat.Domain.Attachments;
using InternalChat.Domain.Conversations;
using InternalChat.Domain.Employees;
using InternalChat.Domain.Meetings;
using InternalChat.Domain.Messages;

namespace InternalChat.UnitTests.Domain;

/// <summary>
/// The domain's exception types, as public API.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these deserve tests rather than a coverage exclusion.</b> Each type carries the standard
/// four constructors that CA1032 requires of a public exception, and three of them are never
/// reached by ordinary code — which makes them look like boilerplate worth excluding. They are not:
/// they are public API that a caller can invoke, and they exist so this exception hierarchy behaves
/// like every other .NET exception when serialized, wrapped, or rethrown. A constructor that threw
/// or dropped its inner exception would be found here and nowhere else.
/// </para>
/// <para>
/// The assertions that carry real weight are the ones about <b>state preserved on the exception</b>
/// — <c>LimitBytes</c>, <c>Capacity</c>, <c>Current</c>. Those values are what the API turns into a
/// response a person acts on: FR-023 requires a refusal to state its limit, and the limit travels
/// on the exception. A constructor that dropped one would produce a technically-correct 413 that
/// tells the sender nothing.
/// </para>
/// </remarks>
public sealed class DomainExceptionTests : UnitTestBase
{
    private static readonly Exception Inner = new InvalidOperationException("inner");

    [Fact]
    public void FileTooLarge_carries_the_limit_the_refusal_must_state()
    {
        FileTooLargeException withState = new(AttachmentKind.Image, 30_000_000, FileConstraints.MaximumImageBytes);

        // FR-023's "with the limit stated" is carried here and rendered by ProblemDetailsHandler.
        Assert.Equal(FileConstraints.MaximumImageBytes, withState.LimitBytes);
        Assert.Equal(30_000_000, withState.DeclaredBytes);
        Assert.Equal(AttachmentKind.Image, withState.Kind);

        AssertStandardConstructors(
            new FileTooLargeException(),
            new FileTooLargeException("message"),
            new FileTooLargeException("message", Inner));
    }

    [Fact]
    public void UnsupportedContentType_names_what_would_have_been_accepted()
    {
        UnsupportedContentTypeException withState = new(
            AttachmentKind.Image, "image/svg+xml", ["image/png", "image/jpeg"]);

        Assert.Equal("image/svg+xml", withState.ContentType);
        Assert.Equal(AttachmentKind.Image, withState.Kind);
        Assert.Contains("image/png", withState.Allowed);

        // A null allow-list must not throw. The message is built from it, and a refusal that
        // crashed while explaining itself would turn a 415 into a 500.
        UnsupportedContentTypeException noAllowList = new(AttachmentKind.Video, "video/x-msvideo", []);
        Assert.Empty(noAllowList.Allowed);

        AssertStandardConstructors(
            new UnsupportedContentTypeException(),
            new UnsupportedContentTypeException("message"),
            new UnsupportedContentTypeException("message", Inner));
    }

    [Fact]
    public void VideoTooLong_distinguishes_a_missing_duration_from_an_excessive_one()
    {
        VideoTooLongException excessive = new(700, FileConstraints.MaximumVideoDurationSeconds);
        VideoTooLongException missing = new(null, FileConstraints.MaximumVideoDurationSeconds);

        Assert.Equal(700, excessive.DeclaredSeconds);
        Assert.Null(missing.DeclaredSeconds);

        // Both state the limit; only one can state what was declared. The messages differ because
        // "your video is too long" is unhelpful to someone who declared no duration at all.
        Assert.NotEqual(excessive.Message, missing.Message);
        Assert.Equal(FileConstraints.MaximumVideoDurationSeconds, missing.LimitSeconds);

        AssertStandardConstructors(
            new VideoTooLongException(),
            new VideoTooLongException("message"),
            new VideoTooLongException("message", Inner));
    }

    [Fact]
    public void UnsupportedCodec_names_the_codec_and_the_alternatives()
    {
        UnsupportedCodecException withState = new("hevc", FileConstraints.AllowedVideoCodecs);

        Assert.Equal("hevc", withState.Codec);
        Assert.Contains("h264", withState.Allowed);

        AssertStandardConstructors(
            new UnsupportedCodecException(),
            new UnsupportedCodecException("message"),
            new UnsupportedCodecException("message", Inner));
    }

    [Fact]
    public void ScanVerdictConflict_records_both_states()
    {
        Guid attachmentId = Guid.CreateVersion7();
        ScanVerdictConflictException withState = new(attachmentId, ScanVerdict.Clean, ScanVerdict.Infected);

        // Both ends of the refused transition. "Cannot change verdict" without them is not
        // diagnosable from a log line.
        Assert.Equal(attachmentId, withState.AttachmentId);
        Assert.Equal(ScanVerdict.Clean, withState.Current);
        Assert.Equal(ScanVerdict.Infected, withState.Attempted);

        AssertStandardConstructors(
            new ScanVerdictConflictException(),
            new ScanVerdictConflictException("message"),
            new ScanVerdictConflictException("message", Inner));
    }

    [Fact]
    public void NotTheAuthor_records_who_tried_and_on_what()
    {
        Guid messageId = Guid.CreateVersion7();
        Guid actorId = Guid.CreateVersion7();

        NotTheAuthorException withState = new(messageId, actorId);

        Assert.Equal(messageId, withState.MessageId);
        Assert.Equal(actorId, withState.ActorId);

        AssertStandardConstructors(
            new NotTheAuthorException(),
            new NotTheAuthorException("message"),
            new NotTheAuthorException("message", Inner));
    }

    [Fact]
    public void EditWindowExpired_states_when_the_window_closed()
    {
        Guid messageId = Guid.CreateVersion7();
        DateTimeOffset closedAt = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

        EditWindowExpiredException withState = new(messageId, closedAt);

        // The UI shows this. "Too late" without a time leaves someone unsure whether they missed
        // it by a minute or a week.
        Assert.Equal(messageId, withState.MessageId);
        Assert.Equal(closedAt, withState.ClosedAt);

        AssertStandardConstructors(
            new EditWindowExpiredException(),
            new EditWindowExpiredException("message"),
            new EditWindowExpiredException("message", Inner));
    }

    [Fact]
    public void MessageDeleted_records_the_tombstone()
    {
        Guid messageId = Guid.CreateVersion7();
        MessageDeletedException withState = new(messageId);

        Assert.Equal(messageId, withState.MessageId);

        AssertStandardConstructors(
            new MessageDeletedException(),
            new MessageDeletedException("message"),
            new MessageDeletedException("message", Inner));
    }

    [Fact]
    public void MeetingFull_carries_the_ceiling_the_client_renders()
    {
        // The client renders this as "25 participants", so the number has to travel.
        Guid meetingId = Guid.CreateVersion7();
        MeetingFullException full = new(meetingId, Meeting.MaximumParticipants);

        Assert.Equal(meetingId, full.MeetingId);
        Assert.Equal(Meeting.MaximumParticipants, full.Capacity);

        AssertStandardConstructors(
            new MeetingFullException(),
            new MeetingFullException("message"),
            new MeetingFullException("message", Inner));
    }

    [Fact]
    public void MeetingEnded_records_which_meeting()
    {
        Guid meetingId = Guid.CreateVersion7();
        MeetingEndedException withState = new(meetingId);

        Assert.Equal(meetingId, withState.MeetingId);

        AssertStandardConstructors(
            new MeetingEndedException(),
            new MeetingEndedException("message"),
            new MeetingEndedException("message", Inner));
    }

    [Fact]
    public void The_conversation_and_employee_exceptions_carry_their_subject()
    {
        Guid conversationId = Guid.CreateVersion7();
        Guid employeeId = Guid.CreateVersion7();

        DirectConversationException direct = new(conversationId, "a direct conversation cannot be renamed");
        MemberAlreadyActiveException already = new(conversationId, employeeId);
        DeactivatedEmployeeException deactivated = new(employeeId);

        Assert.Equal(conversationId, direct.ConversationId);
        Assert.Equal(conversationId, already.ConversationId);
        Assert.Equal(employeeId, already.EmployeeId);
        Assert.Equal(employeeId, deactivated.EmployeeId);

        AssertStandardConstructors(
            new DirectConversationException(),
            new DirectConversationException("message"),
            new DirectConversationException("message", Inner));

        AssertStandardConstructors(
            new MemberAlreadyActiveException(),
            new MemberAlreadyActiveException("message"),
            new MemberAlreadyActiveException("message", Inner));

        AssertStandardConstructors(
            new DeactivatedEmployeeException(),
            new DeactivatedEmployeeException("message"),
            new DeactivatedEmployeeException("message", Inner));
    }

    /// <summary>
    /// Asserts the three standard constructors behave as .NET expects.
    /// </summary>
    /// <remarks>
    /// The inner-exception one is the only one that can meaningfully misbehave, and it is the one
    /// that matters: an exception that dropped its inner would erase the original cause from every
    /// wrapped rethrow, which is the single most expensive thing to lose in a production stack
    /// trace.
    /// </remarks>
    private static void AssertStandardConstructors(
        Exception parameterless,
        Exception withMessage,
        Exception withInner)
    {
        Assert.False(string.IsNullOrWhiteSpace(parameterless.Message));

        Assert.Equal("message", withMessage.Message);
        Assert.Null(withMessage.InnerException);

        Assert.Equal("message", withInner.Message);
        Assert.Same(Inner, withInner.InnerException);
    }
}
