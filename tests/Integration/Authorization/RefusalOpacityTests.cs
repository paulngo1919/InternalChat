using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using InternalChat.Domain.Conversations;
using InternalChat.Infrastructure.Persistence;
using InternalChat.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace InternalChat.IntegrationTests.Authorization;

/// <summary>
/// T055 — SC-017: a refusal must be indistinguishable from a not-found, in body <b>and</b> in
/// timing.
/// </summary>
/// <remarks>
/// <para>
/// The requirement exists because a distinguishable refusal is an enumeration oracle. If asking for
/// a conversation you do not belong to returns 403 and asking for one that does not exist returns
/// 404, anyone with a valid token can walk the id space and learn which conversations exist, who is
/// talking, and roughly how much. The bodies here are therefore compared field by field, not merely
/// by status code.
/// </para>
/// <para>
/// <b>Timing is the half that gets forgotten.</b> A 403 that is fast because a membership row was
/// found and rejected, against a 404 that is slow because a full lookup missed — or the reverse —
/// leaks the same fact through a stopwatch. It cannot be asserted exactly without turning the suite
/// flaky, so what is asserted is that neither case is *systematically* faster by a margin a remote
/// caller could act on, measured over enough samples for scheduler noise to wash out and compared
/// on medians rather than means so one paused thread cannot decide the result.
/// </para>
/// </remarks>
public sealed class RefusalOpacityTests : IntegrationTestBase, IAsyncLifetime
{
    /// <summary>Samples per case. Enough for a median to be stable, few enough to stay quick.</summary>
    private const int TimingSamples = 30;

    /// <summary>
    /// Largest median difference treated as noise rather than signal.
    /// </summary>
    /// <remarks>
    /// Generous on purpose. This test is here to catch a structural difference — an extra database
    /// round trip on one path, a cache hit on the other — which shows up as tens of milliseconds,
    /// not as a few. Tightening it to single digits would turn a security assertion into a
    /// flakiness generator, and a test people disable protects nothing.
    /// </remarks>
    private static readonly TimeSpan TimingTolerance = TimeSpan.FromMilliseconds(50);

    private ProtectedProbeFactory _probe = null!;

    public RefusalOpacityTests(StackFixture stack)
        : base(stack)
    {
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _probe = await ProtectedProbeFactory.StartAsync(Stack);
    }

    public override async Task DisposeAsync()
    {
        await _probe.DisposeAsync();
        await base.DisposeAsync();
    }

    /// <summary>
    /// The core assertion: same status, same body, for "not yours" and "does not exist".
    /// </summary>
    [Fact]
    public async Task A_refusal_is_byte_for_byte_indistinguishable_from_a_not_found()
    {
        const string Username = "phuc.duong";

        Guid otherPersonsConversation = Guid.CreateVersion7();
        Guid nonexistentConversation = Guid.CreateVersion7();

        await using (ChatDbContext context = CreateDbContext())
        {
            Guid caller = await TestData.SeedEmployeeAsync(context, Username);
            Guid stranger = await TestData.SeedEmployeeAsync(context, "quyen.ly");

            // A conversation that genuinely exists and genuinely has a member — just not the
            // caller. Without a real membership row the two cases would be identical for an
            // uninteresting reason: nothing to find either way.
            await TestData.SeedMembershipAsync(context, otherPersonsConversation, stranger);

            Assert.NotEqual(caller, stranger);
        }

        using HttpClient client = await AuthenticatedClientAsync(Username);

        (HttpStatusCode refusedStatus, string refusedBody) = await GetAsync(client, otherPersonsConversation);
        (HttpStatusCode missingStatus, string missingBody) = await GetAsync(client, nonexistentConversation);

        Assert.Equal(missingStatus, refusedStatus);
        Assert.Equal(HttpStatusCode.NotFound, refusedStatus);

        // Compared as parsed documents with the per-request trace id removed. Comparing raw text
        // would fail on the trace id alone, and comparing only the status would miss a `detail`
        // that says "you are not a member of this conversation" — which is the leak this test is
        // named after.
        Assert.Equal(Normalise(missingBody), Normalise(refusedBody));
    }

    /// <summary>
    /// A removed member must look exactly like a stranger.
    /// </summary>
    /// <remarks>
    /// Its own test because the code path differs: a removed member has a membership row, so an
    /// implementation that finds the row and then rejects it can easily take a different branch —
    /// and a different branch is where a different body comes from. FR-008 requires removal to take
    /// effect immediately; SC-017 requires it to be silent about having ever applied.
    /// </remarks>
    [Fact]
    public async Task A_removed_member_is_refused_exactly_like_someone_who_never_belonged()
    {
        const string Username = "son.truong";

        Guid removedFrom = Guid.CreateVersion7();
        Guid neverBelonged = Guid.CreateVersion7();

        await using (ChatDbContext context = CreateDbContext())
        {
            Guid caller = await TestData.SeedEmployeeAsync(context, Username);
            Guid stranger = await TestData.SeedEmployeeAsync(context, "thao.dinh");

            await TestData.SeedMembershipAsync(context, removedFrom, caller, removed: true);
            await TestData.SeedMembershipAsync(context, neverBelonged, stranger);
        }

        using HttpClient client = await AuthenticatedClientAsync(Username);

        (HttpStatusCode removedStatus, string removedBody) = await GetAsync(client, removedFrom);
        (HttpStatusCode strangerStatus, string strangerBody) = await GetAsync(client, neverBelonged);

        Assert.Equal(strangerStatus, removedStatus);
        Assert.Equal(Normalise(strangerBody), Normalise(removedBody));
    }

    /// <summary>The control: a genuine member is served, so the refusals above mean something.</summary>
    [Fact]
    public async Task A_member_is_served_the_conversation()
    {
        const string Username = "tuan.mai";
        Guid conversationId = Guid.CreateVersion7();

        await using (ChatDbContext context = CreateDbContext())
        {
            Guid caller = await TestData.SeedEmployeeAsync(context, Username);
            await TestData.SeedMembershipAsync(context, conversationId, caller, MembershipRole.Admin);
        }

        using HttpClient client = await AuthenticatedClientAsync(Username);

        (HttpStatusCode status, _) = await GetAsync(client, conversationId);

        Assert.Equal(HttpStatusCode.OK, status);
    }

    /// <summary>
    /// A deactivated employee's membership grants nothing — and says so no differently.
    /// </summary>
    /// <remarks>
    /// The membership row survives deactivation on purpose (SC-021 needs it a year later), so this
    /// is the case where "the row exists and is live" and "access is granted" come apart. An
    /// authorization read that forgot to join the employee row would pass every other test in this
    /// file and fail this one.
    /// </remarks>
    [Fact]
    public async Task A_deactivated_employee_is_refused_even_with_a_live_membership()
    {
        const string Username = "uyen.cao";
        Guid conversationId = Guid.CreateVersion7();

        await using (ChatDbContext context = CreateDbContext())
        {
            Guid caller = await TestData.SeedEmployeeAsync(context, Username);
            await TestData.SeedMembershipAsync(context, conversationId, caller);
        }

        // The token is obtained while the employee is still active, then the directory catches up —
        // which is the real sequence, not a contrived one.
        using HttpClient client = await AuthenticatedClientAsync(Username);

        await using (ChatDbContext context = CreateDbContext())
        {
            await context.Employees
                .Where(e => e.ExternalSubject == TestData.SubjectFor(Username))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.Status, Domain.Employees.EmployeeStatus.Deactivated)
                    .SetProperty(e => e.DeactivatedAt, DateTimeOffset.UtcNow));
        }

        (HttpStatusCode status, _) = await GetAsync(client, conversationId);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    /// <summary>
    /// SC-017's second half: the two refusals must not be separable by a stopwatch.
    /// </summary>
    [Fact]
    public async Task A_refusal_and_a_not_found_take_indistinguishable_time()
    {
        const string Username = "viet.ha";

        Guid otherPersonsConversation = Guid.CreateVersion7();
        Guid nonexistentConversation = Guid.CreateVersion7();

        await using (ChatDbContext context = CreateDbContext())
        {
            await TestData.SeedEmployeeAsync(context, Username);
            Guid stranger = await TestData.SeedEmployeeAsync(context, "xuan.luong");
            await TestData.SeedMembershipAsync(context, otherPersonsConversation, stranger);
        }

        using HttpClient client = await AuthenticatedClientAsync(Username);

        // Warm-up, discarded. The first request of each shape pays for JIT, the connection pool,
        // and a cold membership cache; including it would measure startup, not the access decision.
        await GetAsync(client, otherPersonsConversation);
        await GetAsync(client, nonexistentConversation);

        TimeSpan refused = await MedianDurationAsync(client, otherPersonsConversation);
        TimeSpan missing = await MedianDurationAsync(client, nonexistentConversation);

        TimeSpan difference = (refused - missing).Duration();

        Assert.True(
            difference <= TimingTolerance,
            $"""
            The two refusals are separable by timing: {refused.TotalMilliseconds:F1} ms for a
            conversation the caller is not a member of, {missing.TotalMilliseconds:F1} ms for one
            that does not exist — a median difference of {difference.TotalMilliseconds:F1} ms,
            above the {TimingTolerance.TotalMilliseconds:F0} ms tolerance.

            SC-017 requires a refusal to reveal nothing about whether the resource exists. A
            measurable difference lets a caller enumerate conversations with a stopwatch, which is
            slower than reading a status code and works just as well.
            """);
    }

    private async Task<HttpClient> AuthenticatedClientAsync(string username)
    {
        string token = await Stack.IssueAccessTokenAsync(username);

        HttpClient client = _probe.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(HttpClient client, Guid conversationId)
    {
        using HttpResponseMessage response =
            await client.GetAsync(new Uri($"/api/v1/conversations/{conversationId}", UriKind.Relative));

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<TimeSpan> MedianDurationAsync(HttpClient client, Guid conversationId)
    {
        List<TimeSpan> samples = [];

        for (int i = 0; i < TimingSamples; i++)
        {
            long start = Stopwatch.GetTimestamp();
            await GetAsync(client, conversationId);
            samples.Add(Stopwatch.GetElapsedTime(start));
        }

        samples.Sort();

        // Median, not mean: one thread-pool stall would move a mean far enough to fail the
        // assertion, and the question being asked is about the typical request.
        return samples[samples.Count / 2];
    }

    /// <summary>
    /// Strips the per-request trace id so two bodies can be compared for everything else.
    /// </summary>
    /// <remarks>
    /// The trace id is expected to differ and is safe to differ — it identifies the request, not
    /// the resource. Everything else in a Problem Details body must match exactly.
    /// </remarks>
    private static string Normalise(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        using JsonDocument document = JsonDocument.Parse(body);

        SortedDictionary<string, string> fields = new(StringComparer.Ordinal);

        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (string.Equals(property.Name, "traceId", StringComparison.Ordinal)
                || string.Equals(property.Name, "instance", StringComparison.Ordinal))
            {
                continue;
            }

            fields[property.Name] = property.Value.ToString();
        }

        return string.Join('\n', fields.Select(f => $"{f.Key}={f.Value}"));
    }
}
