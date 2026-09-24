using InternalChat.Application.Abstractions;
using InternalChat.Application.Attachments;
using InternalChat.Application.Conversations;
using InternalChat.Application.Notifications;
using InternalChat.Domain.Conversations;
using InternalChat.Domain.Notifications;

namespace InternalChat.UnitTests.Application;

/// <summary>
/// The boundary validators, which decide what a caller is told when a request is malformed.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are not the security control and the tests are written on that understanding.</b>
/// Principle IV puts the real enforcement in the domain — <c>Conversation.CreateDirect</c> refuses a
/// one-person conversation whether or not anything validated it first. What these validators buy is
/// a 400 naming the field instead of a 500 from an exception thrown three layers down, and that is
/// what is asserted: the <em>field name</em> as much as the rejection.
/// </para>
/// <para>
/// Every validator here returns all of a request's problems rather than the first, which is the
/// contract <see cref="IValidator{TRequest}"/> states. Tests assert that too — a validator that
/// short-circuits makes someone fix a form one field per round trip.
/// </para>
/// </remarks>
public sealed class ValidatorTests : UnitTestBase
{
    private static readonly Guid Creator = Guid.CreateVersion7();
    private static readonly Guid Other = Guid.CreateVersion7();

    [Fact]
    public async Task A_direct_conversation_with_exactly_one_other_participant_is_valid()
    {
        ValidationResult result = await Validate(new CreateConversation(
            Creator, ConversationKind.Direct, Name: null, [Other], HistoryVisibility.FromJoin));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task Naming_yourself_as_a_member_is_refused()
    {
        // This is how "a direct conversation with myself" arrives: it satisfies a naive count of
        // two while containing one person. The creator is added automatically.
        ValidationResult result = await Validate(new CreateConversation(
            Creator, ConversationKind.Direct, Name: null, [Creator], HistoryVisibility.FromJoin));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Field == "memberIds");
    }

    [Fact]
    public async Task A_conversation_with_nobody_else_in_it_is_refused()
    {
        ValidationResult result = await Validate(new CreateConversation(
            Creator, ConversationKind.Group, "a group", [], HistoryVisibility.FromJoin));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Field == "memberIds");
    }

    [Fact]
    public async Task A_direct_conversation_with_more_than_two_people_is_refused()
    {
        ValidationResult result = await Validate(new CreateConversation(
            Creator,
            ConversationKind.Direct,
            Name: null,
            [Other, Guid.CreateVersion7()],
            HistoryVisibility.FromJoin));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Field == "memberIds");
    }

    [Fact]
    public async Task A_direct_conversation_may_not_be_named()
    {
        // Named by who is in it. A stored name would let the two participants disagree about what
        // the conversation is called, since each sees it from their own side.
        ValidationResult result = await Validate(new CreateConversation(
            Creator, ConversationKind.Direct, "our chat", [Other], HistoryVisibility.FromJoin));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Field == "name");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_group_conversation_requires_a_name(string? name)
    {
        // Whitespace counts as absent. A group called "   " is a group nobody can refer to.
        ValidationResult result = await Validate(new CreateConversation(
            Creator, ConversationKind.Group, name, [Other], HistoryVisibility.FromJoin));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Field == "name");
    }

    [Fact]
    public async Task A_group_name_is_measured_after_trimming()
    {
        string atTheLimit = new('x', Conversation.MaximumNameLength);

        ValidationResult padded = await Validate(new CreateConversation(
            Creator, ConversationKind.Group, $"  {atTheLimit}  ", [Other], HistoryVisibility.FromJoin));

        // Surrounding whitespace is not content, so it must not be what pushes a name over.
        Assert.True(padded.IsValid);

        ValidationResult tooLong = await Validate(new CreateConversation(
            Creator,
            ConversationKind.Group,
            new string('x', Conversation.MaximumNameLength + 1),
            [Other],
            HistoryVisibility.FromJoin));

        Assert.False(tooLong.IsValid);
        Assert.Contains(tooLong.Errors, error => error.Field == "name");
    }

    [Fact]
    public async Task Every_problem_with_a_create_is_reported_at_once()
    {
        // Two independent problems: the creator listed as a member, and a name on a direct
        // conversation. Reporting one at a time makes someone fix a form one round trip per field.
        ValidationResult result = await Validate(new CreateConversation(
            Creator, ConversationKind.Direct, "named", [Creator], HistoryVisibility.FromJoin));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Field == "memberIds");
        Assert.Contains(result.Errors, error => error.Field == "name");
    }

    [Fact]
    public async Task A_well_formed_upload_request_is_valid()
    {
        ValidationResult result = await Validate(NewUpload());

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("audio")]
    [InlineData("Image")]
    [InlineData("")]
    public async Task An_upload_of_an_unknown_kind_is_refused(string kind)
    {
        // Case-sensitive on purpose: the wire contract says 'image' or 'video', and accepting
        // 'Image' here would mean the parse further in has to accept it too or throw a 500.
        ValidationResult result = await Validate(NewUpload() with { Kind = kind });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Field == "kind");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task An_upload_must_declare_a_positive_size(long byteSize)
    {
        // The declared size is what the limit is checked against before any bytes transfer
        // (FR-023). A zero or negative declaration would pass every ceiling.
        ValidationResult result = await Validate(NewUpload() with { ByteSize = byteSize });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Field == "byteSize");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_upload_needs_a_file_name(string fileName)
    {
        ValidationResult result = await Validate(NewUpload() with { FileName = fileName });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Field == "fileName");
    }

    [Fact]
    public async Task Every_problem_with_an_upload_is_reported_at_once()
    {
        ValidationResult result = await Validate(
            NewUpload() with { Kind = "audio", FileName = " ", ByteSize = 0 });

        Assert.Equal(3, result.Errors.Count);
    }

    [Fact]
    public async Task A_do_not_disturb_window_with_a_real_time_zone_is_valid()
    {
        ValidationResult result = await Validate(NewPreferences());

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task An_unrecognised_time_zone_is_refused_rather_than_thrown()
    {
        // An id no platform has, so this holds wherever the suite runs.
        // The handler constructs the window again and would throw. This validator exists so that
        // arrives as a 400 naming the field instead of an uncaught ArgumentException as a 500.
        ValidationResult result = await Validate(
            NewPreferences() with { TimeZoneId = "Mars/Olympus_Mons" });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Field == "dndStart");
    }

    [Fact]
    public async Task Half_a_do_not_disturb_window_is_refused()
    {
        // A start with no end has no meaning: it either mutes forever or not at all, depending on
        // which reading the notifier takes.
        ValidationResult result = await Validate(NewPreferences() with { DndEnd = null });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Field == "dndStart");
    }

    [Theory]
    [InlineData(NotificationPreference.MinimumDigestAfterMinutes - 1)]
    [InlineData(NotificationPreference.MaximumDigestAfterMinutes + 1)]
    public async Task A_digest_threshold_outside_its_range_is_refused(int minutes)
    {
        ValidationResult result = await Validate(
            NewPreferences() with { DigestAfterMinutes = minutes });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Field == "digestAfterMinutes");
    }

    [Theory]
    [InlineData(NotificationPreference.MinimumDigestAfterMinutes)]
    [InlineData(NotificationPreference.MaximumDigestAfterMinutes)]
    public async Task Both_ends_of_the_digest_range_are_accepted(int minutes)
    {
        // Inclusive at both ends. Asserted because an off-by-one here silently rejects the exact
        // value the settings page offers as its maximum.
        ValidationResult result = await Validate(
            NewPreferences() with { DigestAfterMinutes = minutes });

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task A_push_subscription_with_both_keys_is_valid()
    {
        ValidationResult result = await Validate(NewSubscription());

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("", "auth-secret", "p256dh")]
    [InlineData("   ", "auth-secret", "p256dh")]
    [InlineData("public-key", "", "auth")]
    [InlineData("public-key", "   ", "auth")]
    public async Task A_push_subscription_missing_either_key_is_refused(
        string p256dh,
        string auth,
        string expectedField)
    {
        // Both keys are required to encrypt a payload. A subscription stored without one is a row
        // that fails at send time, for every notification, silently.
        ValidationResult result = await Validate(
            NewSubscription() with { P256dh = p256dh, Auth = auth });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Field == expectedField);
    }

    [Fact]
    public async Task Both_missing_push_keys_are_reported_at_once()
    {
        ValidationResult result = await Validate(NewSubscription() with { P256dh = "", Auth = "" });

        Assert.Equal(2, result.Errors.Count);
    }

    [Fact]
    public async Task Every_validator_refuses_a_null_request()
    {
        // Reached through the pipeline, so never null in practice — but a NullReferenceException
        // from inside a validator is a 500 for what is a programming error, and the guard is what
        // makes it an ArgumentNullException at the boundary instead.
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await new CreateConversationValidator().ValidateAsync(null!));

        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await new RequestUploadValidator().ValidateAsync(null!));

        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await new UpdatePreferencesValidator().ValidateAsync(null!));

        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await new RegisterPushSubscriptionValidator().ValidateAsync(null!));
    }

    private static RequestUpload NewUpload() => new(
        Guid.CreateVersion7(),
        Creator,
        "image",
        "image/png",
        1024,
        DurationSeconds: null,
        "screenshot.png");

    /// <summary>
    /// A valid preference update.
    /// </summary>
    /// <remarks>
    /// <b>"UTC" rather than a real IANA id such as "Asia/Ho_Chi_Minh".</b> Directory.Build.props sets
    /// <c>InvariantGlobalization</c>, so the id set this process can resolve is whatever the host
    /// itself provides — Windows ids on a developer machine, IANA ids from the tz database in the
    /// Linux container. "UTC" is the only id both resolve, and pinning a real one here would make
    /// this suite pass or fail depending on which machine ran it. The unit suite tests the
    /// validator; whether a given id resolves on the deployment target is an integration concern.
    /// </remarks>
    private static UpdatePreferences NewPreferences() => new(
        Creator,
        new TimeOnly(22, 0),
        new TimeOnly(7, 0),
        "UTC",
        DigestAfterMinutes: 15);

    private static RegisterPushSubscription NewSubscription() => new(
        Creator,
        new Uri("https://push.example.test/endpoint/abc"),
        "public-key",
        "auth-secret",
        "Mozilla/5.0");

    private static async Task<ValidationResult> Validate(CreateConversation request) =>
        await new CreateConversationValidator().ValidateAsync(request);

    private static async Task<ValidationResult> Validate(RequestUpload request) =>
        await new RequestUploadValidator().ValidateAsync(request);

    private static async Task<ValidationResult> Validate(UpdatePreferences request) =>
        await new UpdatePreferencesValidator().ValidateAsync(request);

    private static async Task<ValidationResult> Validate(RegisterPushSubscription request) =>
        await new RegisterPushSubscriptionValidator().ValidateAsync(request);
}
