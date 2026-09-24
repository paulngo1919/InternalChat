using System.Net;
using System.Net.Http.Json;
using InternalChat.Api.Contracts;
using InternalChat.Domain.Common;
using InternalChat.Domain.Meetings;
using InternalChat.Infrastructure.Persistence;
using InternalChat.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace InternalChat.IntegrationTests.Meetings;

/// <summary>
/// T200 — a second simultaneous share request is handled by the defined rule rather than left
/// ambiguous (FR-050).
/// </summary>
/// <remarks>
/// <para>
/// <b>FR-050 does not require a particular rule — it requires "one defined, visible rule".</b> The
/// rule chosen is last-writer-wins with the displaced sharer named, and these tests pin it down so
/// it cannot drift: a screen-share rule that changes between releases is exactly the
/// unpredictability the requirement is written against.
/// </para>
/// <para>
/// <b>The concurrency test is the one that needs a real database.</b> The handler stops the current
/// share before starting the next, which is correct sequentially and racy under load: two claims
/// can interleave between the read and the write. What makes that impossible is a partial unique
/// index in PostgreSQL, and no unit test can assert a database constraint.
/// </para>
/// </remarks>
public sealed class ConcurrentShareTests : MessagingTestBase
{
    public ConcurrentShareTests(StackFixture stack)
        : base(stack)
    {
    }

    [Fact]
    public async Task The_first_sharer_claims_a_free_slot_and_displaces_nobody()
    {
        Arrangement arrangement = await ArrangeAsync();

        HttpClient first = await AuthenticatedClientAsync("an.nguyen");

        ScreenShareResponse claim = await ShareAsync(first, arrangement.MeetingId, ShareScopes.Screen);

        Assert.Equal(arrangement.FirstMemberId, claim.EmployeeId);
        Assert.Equal(ShareScopes.Screen, claim.Scope);
        Assert.Null(claim.DisplacedEmployeeId);
    }

    [Fact]
    public async Task A_second_sharer_takes_over_and_the_displaced_one_is_named()
    {
        Arrangement arrangement = await ArrangeAsync();

        HttpClient first = await AuthenticatedClientAsync("an.nguyen");
        HttpClient second = await AuthenticatedClientAsync("binh.tran");

        await ShareAsync(first, arrangement.MeetingId, ShareScopes.Screen);

        ScreenShareResponse takeover = await ShareAsync(
            second, arrangement.MeetingId, ShareScopes.Window);

        // THE assertion for "visible". Without the displaced id the first presenter's share simply
        // vanishes, and they cannot tell a takeover from their connection dropping.
        Assert.Equal(arrangement.FirstMemberId, takeover.DisplacedEmployeeId);
        Assert.Equal(arrangement.SecondMemberId, takeover.EmployeeId);
    }

    [Fact]
    public async Task The_displaced_share_is_recorded_as_superseded_rather_than_merely_stopped()
    {
        Arrangement arrangement = await ArrangeAsync();

        HttpClient first = await AuthenticatedClientAsync("an.nguyen");
        HttpClient second = await AuthenticatedClientAsync("binh.tran");

        await ShareAsync(first, arrangement.MeetingId, ShareScopes.Screen);
        await ShareAsync(second, arrangement.MeetingId, ShareScopes.Window);

        await using ChatDbContext context = CreateDbContext();

        ShareSession displaced = await context.ShareSessions
            .AsNoTracking()
            .FirstAsync(s => s.MeetingId == arrangement.MeetingId
                && s.EmployeeId == arrangement.FirstMemberId);

        // The reason is what an audit reader needs to reconstruct what happened, and what the
        // client needs to say something more useful than "sharing stopped".
        Assert.Equal(ShareStopReason.Superseded, displaced.StopReason);
        Assert.NotNull(displaced.StoppedAt);
    }

    [Fact]
    public async Task Exactly_one_share_is_active_after_a_takeover()
    {
        Arrangement arrangement = await ArrangeAsync();

        HttpClient first = await AuthenticatedClientAsync("an.nguyen");
        HttpClient second = await AuthenticatedClientAsync("binh.tran");

        await ShareAsync(first, arrangement.MeetingId, ShareScopes.Screen);
        await ShareAsync(second, arrangement.MeetingId, ShareScopes.Window);

        await using ChatDbContext context = CreateDbContext();

        List<ShareSession> active = await context.ShareSessions
            .AsNoTracking()
            .Where(s => s.MeetingId == arrangement.MeetingId && s.StoppedAt == null)
            .ToListAsync();

        // The state FR-050 forbids, asserted directly. Two rows here is the failure the whole
        // single-publisher rule exists to prevent.
        Assert.Single(active);
        Assert.Equal(arrangement.SecondMemberId, active[0].EmployeeId);
    }

    [Fact]
    public async Task Concurrent_claims_cannot_both_win()
    {
        Arrangement arrangement = await ArrangeAsync();

        HttpClient first = await AuthenticatedClientAsync("an.nguyen");
        HttpClient second = await AuthenticatedClientAsync("binh.tran");

        // Fired together. The handler reads the current share and then writes, so two requests can
        // interleave between those steps — the sequential logic is not what makes this safe.
        Task<HttpResponseMessage>[] claims =
        [
            first.PostAsJsonAsync(ShareRoute(arrangement.MeetingId), new StartScreenShareRequest(ShareScopes.Screen)),
            second.PostAsJsonAsync(ShareRoute(arrangement.MeetingId), new StartScreenShareRequest(ShareScopes.Window)),
        ];

        await Task.WhenAll(claims);

        await using ChatDbContext context = CreateDbContext();

        List<ShareSession> active = await context.ShareSessions
            .AsNoTracking()
            .Where(s => s.MeetingId == arrangement.MeetingId && s.StoppedAt == null)
            .ToListAsync();

        // At most one survives, whichever it is. The partial unique index in PostgreSQL is what
        // guarantees this — the loser's insert is refused rather than both being accepted, and no
        // amount of care in the handler could establish it alone.
        Assert.True(
            active.Count <= 1,
            $"{active.Count} shares are active at once. FR-050 permits exactly one, and the "
            + "partial unique index on share_session is what enforces it.");
    }

    [Fact]
    public async Task Re_claiming_your_own_slot_is_idempotent_and_displaces_nobody()
    {
        Arrangement arrangement = await ArrangeAsync();

        HttpClient first = await AuthenticatedClientAsync("an.nguyen");

        ScreenShareResponse initial = await ShareAsync(first, arrangement.MeetingId, ShareScopes.Screen);
        ScreenShareResponse again = await ShareAsync(first, arrangement.MeetingId, ShareScopes.Screen);

        // A client that re-sends its claim on reconnect must not produce a Superseded record
        // naming the same person as both parties.
        Assert.Equal(initial.StartedAt, again.StartedAt);
        Assert.Null(again.DisplacedEmployeeId);
    }

    [Fact]
    public async Task Stopping_frees_the_slot_for_someone_else()
    {
        Arrangement arrangement = await ArrangeAsync();

        HttpClient first = await AuthenticatedClientAsync("an.nguyen");
        HttpClient second = await AuthenticatedClientAsync("binh.tran");

        await ShareAsync(first, arrangement.MeetingId, ShareScopes.Screen);

        HttpResponseMessage stopped = await first.DeleteAsync(ShareRoute(arrangement.MeetingId));
        Assert.Equal(HttpStatusCode.NoContent, stopped.StatusCode);

        ScreenShareResponse next = await ShareAsync(second, arrangement.MeetingId, ShareScopes.Window);

        // Nobody displaced: the slot was free, so this is not a takeover.
        Assert.Null(next.DisplacedEmployeeId);
    }

    [Fact]
    public async Task A_superseded_participant_stopping_is_accepted_rather_than_an_error()
    {
        Arrangement arrangement = await ArrangeAsync();

        HttpClient first = await AuthenticatedClientAsync("an.nguyen");
        HttpClient second = await AuthenticatedClientAsync("binh.tran");

        await ShareAsync(first, arrangement.MeetingId, ShareScopes.Screen);
        await ShareAsync(second, arrangement.MeetingId, ShareScopes.Window);

        // The displaced client still sends a stop when it closes its picker. Turning that into an
        // error would surface a failure nobody caused and nobody can act on.
        HttpResponseMessage stopped = await first.DeleteAsync(ShareRoute(arrangement.MeetingId));

        Assert.Equal(HttpStatusCode.NoContent, stopped.StatusCode);

        // And it must not have stopped the second person's share.
        await using ChatDbContext context = CreateDbContext();

        Assert.Single(await context.ShareSessions
            .AsNoTracking()
            .Where(s => s.MeetingId == arrangement.MeetingId && s.StoppedAt == null)
            .ToListAsync());
    }

    [Fact]
    public async Task A_non_member_cannot_claim_the_slot()
    {
        Arrangement arrangement = await ArrangeAsync();

        HttpClient outsider = await AuthenticatedClientAsync("chi.le");

        HttpResponseMessage response = await outsider.PostAsJsonAsync(
            ShareRoute(arrangement.MeetingId), new StartScreenShareRequest(ShareScopes.Screen));

        // Membership is re-checked on every share claim, not trusted from the join token — that
        // token was minted up to five minutes ago and cannot be revoked (FR-030).
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_scope_is_refused()
    {
        Arrangement arrangement = await ArrangeAsync();

        HttpClient first = await AuthenticatedClientAsync("an.nguyen");

        HttpResponseMessage response = await first.PostAsJsonAsync(
            ShareRoute(arrangement.MeetingId), new StartScreenShareRequest("everything"));

        // Defaulting an unrecognised scope to Screen would record a whole-screen share as though
        // the person had chosen it — the precise claim FR-048 makes about the window case.
        Assert.False(response.IsSuccessStatusCode);
    }

    private static string ShareRoute(Guid meetingId) => $"/api/v1/meetings/{meetingId}/share";

    private static async Task<ScreenShareResponse> ShareAsync(
        HttpClient client,
        Guid meetingId,
        string scope)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            ShareRoute(meetingId), new StartScreenShareRequest(scope));

        Assert.True(
            response.IsSuccessStatusCode,
            $"Claiming the share slot returned {(int)response.StatusCode}: "
            + await response.Content.ReadAsStringAsync());

        ScreenShareResponse? claim = await response.Content.ReadFromJsonAsync<ScreenShareResponse>();
        Assert.NotNull(claim);

        return claim;
    }

    private async Task<Arrangement> ArrangeAsync()
    {
        Guid conversationId = Guid.CreateVersion7();

        await using ChatDbContext context = CreateDbContext();

        Guid first = await TestData.SeedEmployeeAsync(context, "an.nguyen");
        Guid second = await TestData.SeedEmployeeAsync(context, "binh.tran");
        await TestData.SeedEmployeeAsync(context, "chi.le");

        await TestData.SeedMembershipAsync(context, conversationId, first);
        await TestData.SeedMembershipAsync(context, conversationId, second);

        Meeting meeting = Meeting.Start(Guid.CreateVersion7(), conversationId, first, new FixedClock());
        meeting.ClearDomainEvents();

        context.Meetings.Add(meeting);
        await context.SaveChangesAsync();

        return new Arrangement(meeting.Id, first, second);
    }

    private sealed record Arrangement(Guid MeetingId, Guid FirstMemberId, Guid SecondMemberId);
}
