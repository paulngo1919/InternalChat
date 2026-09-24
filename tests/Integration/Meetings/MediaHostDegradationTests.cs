using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InternalChat.Api.Contracts;
using InternalChat.Domain.Attachments;
using InternalChat.Domain.Common;
using InternalChat.Domain.Messages;
using InternalChat.Infrastructure.Persistence;
using InternalChat.IntegrationTests.Fixtures;

namespace InternalChat.IntegrationTests.Meetings;

/// <summary>
/// T182 — with the media host unreachable, meetings report unavailable while messaging,
/// attachments, search, and notifications keep working.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the test that justifies the two-host architecture.</b> Constitution v1.2.0 permits a
/// second machine for exactly one thing, and requires in exchange that "the application must stay
/// fully functional with the media host down". That is a claim about the whole platform, not about
/// the meetings feature, so the assertions below are mostly about the <em>other</em> features —
/// sending a message, reserving an upload, running a search — each of which must be untouched.
/// </para>
/// <para>
/// <b>The media host is unreachable by default here, which makes this the cheap test and the
/// reachable case the expensive one.</b> There is no LiveKit container in the integration stack;
/// <c>ApiFactory</c> points at a closed port. That is deliberate: this property is the one worth
/// having continuously verified, and a suite that needed an SFU to assert it would be skipped.
/// </para>
/// <para>
/// A closed port rather than a black-holed address, so a refused connection is immediate. A
/// timeout would make every assertion here slow and would test the HTTP client's timeout rather
/// than the degradation path.
/// </para>
/// </remarks>
public sealed class MediaHostDegradationTests : MessagingTestBase
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public MediaHostDegradationTests(StackFixture stack)
        : base(stack)
    {
    }

    [Fact]
    public async Task Starting_a_meeting_reports_unavailable_rather_than_failing_obscurely()
    {
        Guid conversationId = (await ArrangeAsync()).ConversationId;

        HttpClient client = await AuthenticatedClientAsync("an.nguyen");
        HttpResponseMessage response = await client.PostAsync(
            $"/api/v1/conversations/{conversationId}/meetings", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // retryAfterSeconds, so a client waits rather than hammering a host that is down.
        Assert.True(problem.RootElement.TryGetProperty("retryAfterSeconds", out JsonElement retry));
        Assert.True(retry.GetInt32() > 0);

        // And it says messaging is fine. A client that cannot tell "meetings are down" from "the
        // platform is down" will tell its user the wrong thing — which is the failure the whole
        // two-host split exists to avoid.
        string title = problem.RootElement.GetProperty("title").GetString() ?? string.Empty;
        Assert.Contains("Messaging", title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sending_a_message_is_unaffected()
    {
        Guid conversationId = (await ArrangeAsync()).ConversationId;

        HttpClient client = await AuthenticatedClientAsync("an.nguyen");

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/v1/conversations/{conversationId}/messages",
            new SendMessageRequest("01JBXQ7ZPT4M9WYFN2VKC3H6RD", "the media host is down", null, null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Reading_history_is_unaffected()
    {
        Guid conversationId = (await ArrangeAsync()).ConversationId;

        HttpClient client = await AuthenticatedClientAsync("an.nguyen");

        await client.PostAsJsonAsync(
            $"/api/v1/conversations/{conversationId}/messages",
            new SendMessageRequest("01JBXQ7ZPT4M9WYFN2VKC3H6RE", "still here", null, null));

        MessagePageResponse? page = await client.GetFromJsonAsync<MessagePageResponse>(
            $"/api/v1/conversations/{conversationId}/messages");

        Assert.NotNull(page);
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task Reserving_an_attachment_upload_is_unaffected()
    {
        Guid conversationId = (await ArrangeAsync()).ConversationId;

        HttpClient client = await AuthenticatedClientAsync("an.nguyen");

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/v1/conversations/{conversationId}/attachments",
            new RequestUploadRequest(AttachmentKinds.Image, "image/png", 4096, null, "shot.png"));

        // Attachments go to MinIO on the application host and have nothing to do with the media
        // host. Asserted because "fully functional" is a claim about every feature, and a shared
        // failure path — a health gate, a global circuit breaker — would take this down with it.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Searching_is_unaffected()
    {
        Arrangement arrangement = await ArrangeAsync();

        await using (ChatDbContext context = CreateDbContext())
        {
            Message message = Message.Send(
                Guid.CreateVersion7(),
                arrangement.ConversationId,
                seq: 1,
                arrangement.AuthorId,
                ClientMessageKey.Parse("01JBXQ7ZPT4M9WYFN2VKC3H6RF"),
                MessageBody.Create("quarterly aubergine planning"),
                new FixedClock());

            message.ClearDomainEvents();
            context.Messages.Add(message);
            await context.SaveChangesAsync();
        }

        HttpClient client = await AuthenticatedClientAsync("an.nguyen");

        SearchResultPageResponse? page = await client.GetFromJsonAsync<SearchResultPageResponse>(
            "/api/v1/search/messages?q=aubergine");

        Assert.NotNull(page);
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task Notification_preferences_are_unaffected()
    {
        await ArrangeAsync();

        HttpClient client = await AuthenticatedClientAsync("an.nguyen");
        HttpResponseMessage response = await client.GetAsync("/api/v1/notifications/preferences");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_platform_reports_itself_healthy_while_meetings_are_down()
    {
        await ArrangeAsync();

        // The readiness probe must NOT fail because of the media host. If it did, an orchestrator
        // would take the application host out of rotation over a second-host outage — turning a
        // degraded feature into a total outage, which is the exact inversion of what the two-host
        // split is for.
        HttpResponseMessage ready = await Api.CreateClient().GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    /// <summary>Seeds one employee in one conversation, returning both ids.</summary>
    /// <remarks>
    /// The author id is returned rather than re-derived by callers. <c>SeedEmployeeAsync</c> is not
    /// idempotent — <c>employee.email</c> is uniquely indexed — so a test that seeded the same
    /// person twice would fail on a constraint violation that says nothing about what it was testing.
    /// </remarks>
    private async Task<Arrangement> ArrangeAsync()
    {
        Guid conversationId = Guid.CreateVersion7();

        await using ChatDbContext context = CreateDbContext();

        Guid author = await TestData.SeedEmployeeAsync(context, "an.nguyen");
        await TestData.SeedMembershipAsync(context, conversationId, author);

        return new Arrangement(conversationId, author);
    }

    private sealed record Arrangement(Guid ConversationId, Guid AuthorId);
}
