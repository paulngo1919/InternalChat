using InternalChat.Application.Abstractions;
using InternalChat.Application.Attachments;
using InternalChat.Application.Behaviors;
using InternalChat.Application.Meetings;
using InternalChat.Application.Messages;

namespace InternalChat.UnitTests.Application;

/// <summary>
/// The Application layer's exception types, each of which maps to a specific HTTP status.
/// </summary>
/// <remarks>
/// <para>
/// <b>These types exist because the status code matters.</b> A 507 tells a client that attachment
/// storage is full and retrying later may work; a 503 tells it the media host is down; a 422 tells
/// it the request was understood and refused. Collapsing them into one failure type would be
/// simpler and would leave every client unable to tell a transient condition from a permanent one.
/// The state each carries — <c>UsedBytes</c>, <c>CurrentParticipants</c>, <c>AttachmentIds</c> — is
/// what <c>ProblemDetailsHandler</c> turns into a response someone can act on.
/// </para>
/// <para>
/// The three standard constructors are public API that CA1032 requires and that ordinary code never
/// reaches. They are asserted anyway: an exception that dropped its inner would erase the original
/// cause from every wrapped rethrow, which is the most expensive thing to lose from a production
/// stack trace and the least likely to be noticed.
/// </para>
/// </remarks>
public sealed class ApplicationExceptionTests : UnitTestBase
{
    private static readonly Exception Inner = new InvalidOperationException("inner");

    [Fact]
    public void An_infected_attachment_names_which_one()
    {
        Guid attachmentId = Guid.CreateVersion7();
        AttachmentInfectedException withState = new(attachmentId);

        Assert.Equal(attachmentId, withState.AttachmentId);

        AssertStandardConstructors(
            new AttachmentInfectedException(),
            new AttachmentInfectedException("message"),
            new AttachmentInfectedException("message", Inner));
    }

    [Fact]
    public void An_unknown_attachment_kind_echoes_what_the_client_sent()
    {
        UnsupportedAttachmentKindException withState = new("audio");

        Assert.Equal("audio", withState.Kind);

        // Echoing the value is what makes the 400 actionable: "'audio' is not an attachment kind"
        // beats "invalid kind" when the caller is a client library sending a typo.
        Assert.Contains("audio", withState.Message, StringComparison.Ordinal);

        // No (string) constructor here — ArgumentException's single-string overload is the
        // parameter name, not a message, so exposing it would silently mislabel every message.
        Assert.False(string.IsNullOrWhiteSpace(new UnsupportedAttachmentKindException().Message));

        UnsupportedAttachmentKindException withInner = new("message", Inner);

        Assert.Same(Inner, withInner.InnerException);
    }

    [Fact]
    public void Storage_capacity_carries_both_figures_the_operator_needs()
    {
        StorageCapacityExceededException withState = new(950_000_000, 1_000_000_000);

        // Used and provisioned. Either alone is unactionable — "storage is full" does not say
        // whether the fix is a bigger volume or a retention sweep that failed to run.
        Assert.Equal(950_000_000, withState.UsedBytes);
        Assert.Equal(1_000_000_000, withState.CapacityBytes);

        // FR-028: the refusal states that text messaging is unaffected, because the first question
        // asked when uploads start failing is whether the platform is down.
        Assert.Contains("Text messaging is unaffected", withState.Message, StringComparison.Ordinal);

        AssertStandardConstructors(
            new StorageCapacityExceededException(),
            new StorageCapacityExceededException("message"),
            new StorageCapacityExceededException("message", Inner));
    }

    [Fact]
    public void A_media_host_outage_is_its_own_condition()
    {
        // No state: the client cannot do anything with a LiveKit address, and there is nothing to
        // say beyond "not now". The type is what carries the meaning, and it maps to 503.
        AssertStandardConstructors(
            new MediaHostUnavailableException(),
            new MediaHostUnavailableException("message"),
            new MediaHostUnavailableException("message", Inner));
    }

    [Fact]
    public void Platform_capacity_states_how_many_are_already_in_meetings()
    {
        PlatformAtCapacityException withState = new(100);

        Assert.Equal(100, withState.CurrentParticipants);

        AssertStandardConstructors(
            new PlatformAtCapacityException(),
            new PlatformAtCapacityException("message"),
            new PlatformAtCapacityException("message", Inner));
    }

    [Fact]
    public void An_unattachable_attachment_names_every_offending_id_not_just_the_first()
    {
        Guid first = Guid.CreateVersion7();
        Guid second = Guid.CreateVersion7();

        AttachmentNotAttachableException withState = new([first, second]);

        // All of them. Reporting one at a time makes a client retry a multi-attachment message once
        // per bad attachment, and each retry costs the whole upload.
        Assert.Equal([first, second], withState.AttachmentIds);

        // The default is empty rather than null, so a caller reading it after catching one of the
        // parameterless constructions does not get a NullReferenceException on top of the original
        // failure.
        Assert.Empty(new AttachmentNotAttachableException().AttachmentIds);

        AssertStandardConstructors(
            new AttachmentNotAttachableException(),
            new AttachmentNotAttachableException("message"),
            new AttachmentNotAttachableException("message", Inner));
    }

    [Fact]
    public void A_validation_failure_carries_every_field_error()
    {
        ValidationError[] errors =
        [
            new("name", "A group conversation requires a name."),
            new("memberIds", "Name at least one other participant."),
        ];

        ValidationFailedException withState = new(errors);

        // These become the RFC 9457 `errors` member. Losing one turns a complete 400 into a form
        // the sender has to fix twice.
        Assert.Equal(2, withState.Errors.Count);
        Assert.Contains(withState.Errors, error => error.Field == "name");

        Assert.Empty(new ValidationFailedException().Errors);

        AssertStandardConstructors(
            new ValidationFailedException(),
            new ValidationFailedException("message"),
            new ValidationFailedException("message", Inner));
    }

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
