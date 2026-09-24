using System.Net;
using System.Net.Http.Json;
using InternalChat.Api.Contracts;
using InternalChat.Infrastructure.Persistence;
using InternalChat.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace InternalChat.IntegrationTests.Conversations;

/// <summary>
/// T111 — a removed member immediately loses access to new messages (US3 scenario 3).
/// </summary>
/// <remarks>
/// Attachment access is not exercised here — attachments do not exist until US5 — so this covers the
/// part of scenario 3 that US3 can actually deliver: history and new messages. T146 revisits removal
/// for the attachment path once it exists.
/// </remarks>
public sealed class RemovalTests : MessagingTestBase
{
    public RemovalTests(StackFixture stack)
        : base(stack)
    {
    }

    /// <summary>
    /// A removed member is refused both new history and the conversation itself, indistinguishably
    /// from a conversation that never existed (SC-017) — while a remaining member keeps reading.
    /// </summary>
    [Fact]
    public async Task A_removed_member_is_refused_the_conversation_while_a_remaining_member_still_reads_it()
    {
        const string Admin = "an.nguyen";
        const string StaysIn = "binh.tran";
        const string RemovedMember = "chi.le";

        (_, Guid staysInId, Guid removedId) = await SeedThreeAsync(Admin, StaysIn, RemovedMember);

        using HttpClient adminClient = await AuthenticatedClientAsync(Admin);

        Guid conversationId = await CreateGroupAsync(adminClient, "removal-test", [staysInId, removedId]);

        using HttpResponseMessage removeResponse = await adminClient.DeleteAsync(
            new Uri($"/api/v1/conversations/{conversationId}/members/{removedId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NoContent, removeResponse.StatusCode);

        using HttpResponseMessage sendResponse = await SendAsync(
            adminClient, conversationId, ClientKey(9001), "posted after the removal");
        Assert.Equal(HttpStatusCode.Created, sendResponse.StatusCode);

        using HttpClient removedClient = await AuthenticatedClientAsync(RemovedMember);
        using HttpClient staysInClient = await AuthenticatedClientAsync(StaysIn);

        // Refused, and shaped as a not-found — not a distinct "forbidden" — for the same reason every
        // other resource-scoped refusal in this platform is (SC-017).
        using HttpResponseMessage removedHistory = await removedClient.GetAsync(
            new Uri($"/api/v1/conversations/{conversationId}/messages", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, removedHistory.StatusCode);

        using HttpResponseMessage removedDetail = await removedClient.GetAsync(
            new Uri($"/api/v1/conversations/{conversationId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, removedDetail.StatusCode);

        MessagePageResponse page = await HistoryAsync(staysInClient, conversationId);
        Assert.Contains(page.Items, m => m.Body == "posted after the removal");
    }

    /// <summary>The removal is recorded in the audit log with actor, subject, and outcome (FR-006).</summary>
    [Fact]
    public async Task The_removal_is_recorded_in_the_audit_log()
    {
        const string Admin = "dung.pham";
        const string OtherMember = "giang.hoang";
        const string RemovedMember = "hai.vo";

        (Guid adminId, _, Guid removedId) = await SeedThreeAsync(Admin, OtherMember, RemovedMember);

        using HttpClient adminClient = await AuthenticatedClientAsync(Admin);

        Guid conversationId = await CreateGroupAsync(
            adminClient, "removal-audit-test", [await EmployeeIdAsync(OtherMember), removedId]);

        using HttpResponseMessage removeResponse = await adminClient.DeleteAsync(
            new Uri($"/api/v1/conversations/{conversationId}/members/{removedId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NoContent, removeResponse.StatusCode);

        await using ChatDbContext context = CreateDbContext();

        bool recorded = await context.AuditEvents.AnyAsync(a =>
            a.Action == "membership.removed"
            && a.ActorId == adminId
            && a.SubjectType == "conversation"
            && a.SubjectId == conversationId
            && a.Outcome == "success");

        Assert.True(recorded, "Removing a member must be recorded in the audit log (FR-006, spec.md US1 scenario 5).");
    }

    private async Task<(Guid Admin, Guid Other, Guid Removed)> SeedThreeAsync(
        string admin, string other, string removed)
    {
        await using ChatDbContext context = CreateDbContext();

        Guid adminId = await TestData.SeedEmployeeAsync(context, admin);
        Guid otherId = await TestData.SeedEmployeeAsync(context, other);
        Guid removedId = await TestData.SeedEmployeeAsync(context, removed);

        return (adminId, otherId, removedId);
    }

    private async Task<Guid> EmployeeIdAsync(string username)
    {
        await using ChatDbContext context = CreateDbContext();

        return await context.Employees.Where(e => e.ExternalSubject == TestData.SubjectFor(username))
            .Select(e => e.Id)
            .SingleAsync();
    }

    private static async Task<Guid> CreateGroupAsync(HttpClient adminClient, string name, IReadOnlyList<Guid> memberIds)
    {
        using HttpResponseMessage response = await adminClient.PostAsJsonAsync(
            new Uri("/api/v1/conversations", UriKind.Relative),
            new CreateConversationRequest("group", name, memberIds, "from_join"));

        response.EnsureSuccessStatusCode();

        ConversationResponse conversation = await response.Content.ReadFromJsonAsync<ConversationResponse>()
            ?? throw new InvalidOperationException("Conversation creation returned an empty body.");

        return conversation.Id;
    }
}
