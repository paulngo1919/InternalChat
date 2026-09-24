using System.Net;
using System.Net.Http.Json;
using InternalChat.Api.Contracts;
using InternalChat.Domain.Common;
using InternalChat.Domain.Meetings;
using InternalChat.Infrastructure.Persistence;
using InternalChat.IntegrationTests.Fixtures;

namespace InternalChat.IntegrationTests.Meetings;

/// <summary>
/// T181 — a non-member is refused a join token (FR-041).
/// </summary>
/// <remarks>
/// <para>
/// <b>This endpoint is the entire meeting security boundary, which is why it gets its own test
/// file.</b> The media server trusts its token completely — it runs no membership check of its own
/// and cannot, because it knows nothing about conversations. If a token is ever minted for someone
/// who should not have one, nothing downstream will catch it: they will simply be in the meeting.
/// </para>
/// <para>
/// The tests therefore go after the ways a caller might try to obtain one: as a non-member, for an
/// ended meeting, for a meeting that does not exist, and after losing membership. Each must refuse
/// identically, so none of them is also an oracle for which meeting ids are real.
/// </para>
/// </remarks>
public sealed class TokenAuthorizationTests : MessagingTestBase
{
    public TokenAuthorizationTests(StackFixture stack)
        : base(stack)
    {
    }

    [Fact]
    public async Task A_non_member_is_refused_a_join_token()
    {
        Arrangement arrangement = await ArrangeAsync();

        HttpClient outsider = await AuthenticatedClientAsync("chi.le");
        HttpResponseMessage response = await outsider.PostAsync(TokenRoute(arrangement.MeetingId), null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_refused_token_is_indistinguishable_from_a_meeting_that_never_existed()
    {
        Arrangement arrangement = await ArrangeAsync();

        HttpClient outsider = await AuthenticatedClientAsync("chi.le");

        HttpResponseMessage real = await outsider.PostAsync(TokenRoute(arrangement.MeetingId), null);
        HttpResponseMessage invented = await outsider.PostAsync(TokenRoute(Guid.CreateVersion7()), null);

        // Otherwise the endpoint is an oracle for which meeting ids are real (SC-017).
        Assert.Equal(invented.StatusCode, real.StatusCode);
    }

    [Fact]
    public async Task An_ended_meeting_issues_no_token_even_to_a_member()
    {
        Arrangement arrangement = await ArrangeAsync();

        await using (ChatDbContext context = CreateDbContext())
        {
            Meeting meeting = await context.Meetings.FindAsync(arrangement.MeetingId)
                ?? throw new InvalidOperationException("The seeded meeting is missing.");

            meeting.End(new FixedClock());
            meeting.ClearDomainEvents();

            await context.SaveChangesAsync();
        }

        HttpClient member = await AuthenticatedClientAsync("an.nguyen");
        HttpResponseMessage response = await member.PostAsync(TokenRoute(arrangement.MeetingId), null);

        // A token for a finished meeting would admit someone to a LiveKit room that is still open
        // for its empty-timeout window — a call nobody else can see, in a conversation's name.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Losing_membership_stops_a_token_being_issued()
    {
        Arrangement arrangement = await ArrangeAsync();

        HttpClient second = await AuthenticatedClientAsync("binh.tran");

        // The control: they could get one a moment ago.
        Assert.NotEqual(
            HttpStatusCode.NotFound,
            (await second.PostAsync(TokenRoute(arrangement.MeetingId), null)).StatusCode);

        await using (ChatDbContext context = CreateDbContext())
        {
            Domain.Conversations.Membership membership = await context.Memberships
                .FindAsync(arrangement.ConversationId, arrangement.SecondMemberId)
                ?? throw new InvalidOperationException("The seeded membership is missing.");

            membership.Remove(new FixedClock());
            await context.SaveChangesAsync();
        }

        await ResetCacheAsync();

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await second.PostAsync(TokenRoute(arrangement.MeetingId), null)).StatusCode);
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_refused()
    {
        Arrangement arrangement = await ArrangeAsync();

        HttpResponseMessage response = await Api.CreateClient()
            .PostAsync(TokenRoute(arrangement.MeetingId), null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_non_member_cannot_start_a_meeting_either()
    {
        Arrangement arrangement = await ArrangeAsync();

        HttpClient outsider = await AuthenticatedClientAsync("chi.le");

        HttpResponseMessage response = await outsider.PostAsync(
            $"/api/v1/conversations/{arrangement.ConversationId}/meetings", null);

        // The start route carries the membership filter, so this is refused before the handler.
        // Asserted anyway: the two routes are authorized by different mechanisms, and a test that
        // only covered the token route would not notice the filter being dropped from this one.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static string TokenRoute(Guid meetingId) => $"/api/v1/meetings/{meetingId}/token";

    private async Task<Arrangement> ArrangeAsync()
    {
        Guid conversationId = Guid.CreateVersion7();

        await using ChatDbContext context = CreateDbContext();

        Guid author = await TestData.SeedEmployeeAsync(context, "an.nguyen");
        Guid second = await TestData.SeedEmployeeAsync(context, "binh.tran");
        await TestData.SeedEmployeeAsync(context, "chi.le");

        await TestData.SeedMembershipAsync(context, conversationId, author);
        await TestData.SeedMembershipAsync(context, conversationId, second);

        // Seeded directly rather than started through the API: starting requires a reachable media
        // host, and these tests are about who may join an existing meeting rather than about how
        // one comes into being.
        Meeting meeting = Meeting.Start(Guid.CreateVersion7(), conversationId, author, new FixedClock());
        meeting.ClearDomainEvents();

        context.Meetings.Add(meeting);
        await context.SaveChangesAsync();

        return new Arrangement(conversationId, meeting.Id, second);
    }

    private sealed record Arrangement(Guid ConversationId, Guid MeetingId, Guid SecondMemberId);
}
