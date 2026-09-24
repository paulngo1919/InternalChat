using System.Net.Http.Json;
using InternalChat.Api.Contracts;
using InternalChat.Infrastructure.Persistence;
using InternalChat.IntegrationTests.Fixtures;

namespace InternalChat.IntegrationTests.Conversations;

/// <summary>
/// T112 — a newly added member sees history according to the conversation's stated rule (US3
/// scenario 4).
/// </summary>
/// <remarks>
/// Both rules are exercised, not just the default: <c>from_join</c> is what most groups use, but
/// <c>full</c> is a real, chosen setting (openapi.yaml <c>CreateConversationRequest.historyVisibility</c>)
/// and a test that only covered the default could not tell "always hides prior history" from
/// "respects the rule".
/// </remarks>
public sealed class HistoryVisibilityTests : MessagingTestBase
{
    public HistoryVisibilityTests(StackFixture stack)
        : base(stack)
    {
    }

    [Fact]
    public async Task A_from_join_group_hides_history_sent_before_the_new_member_joined()
    {
        const string Admin = "an.nguyen";
        const string Existing = "binh.tran";
        const string NewMember = "chi.le";

        (_, Guid existingId, Guid newMemberId) = await SeedThreeAsync(Admin, Existing, NewMember);

        using HttpClient adminClient = await AuthenticatedClientAsync(Admin);

        Guid conversationId = await CreateGroupAsync(
            adminClient, "from-join-test", [existingId], historyVisibility: "from_join");

        for (int i = 0; i < 3; i++)
        {
            using HttpResponseMessage before = await SendAsync(
                adminClient, conversationId, ClientKey(9100 + i), $"before the new member joined {i}");
            before.EnsureSuccessStatusCode();
        }

        using HttpResponseMessage addResponse = await adminClient.PostAsJsonAsync(
            new Uri($"/api/v1/conversations/{conversationId}/members", UriKind.Relative),
            new AddMemberRequest(newMemberId, Role: null));
        addResponse.EnsureSuccessStatusCode();

        using HttpClient newMemberClient = await AuthenticatedClientAsync(NewMember);

        MessagePageResponse beforeAnyNewMessage = await HistoryAsync(newMemberClient, conversationId);
        Assert.Empty(beforeAnyNewMessage.Items);

        using HttpResponseMessage afterJoin = await SendAsync(
            adminClient, conversationId, ClientKey(9200), "after the new member joined");
        afterJoin.EnsureSuccessStatusCode();

        MessagePageResponse afterJoinPage = await HistoryAsync(newMemberClient, conversationId);

        Assert.Single(afterJoinPage.Items);
        Assert.Equal("after the new member joined", afterJoinPage.Items[0].Body);
    }

    [Fact]
    public async Task A_full_visibility_group_shows_a_new_member_everything_sent_before_they_joined()
    {
        const string Admin = "dung.pham";
        const string Existing = "giang.hoang";
        const string NewMember = "hai.vo";

        (_, Guid existingId, Guid newMemberId) = await SeedThreeAsync(Admin, Existing, NewMember);

        using HttpClient adminClient = await AuthenticatedClientAsync(Admin);

        Guid conversationId = await CreateGroupAsync(
            adminClient, "full-history-test", [existingId], historyVisibility: "full");

        for (int i = 0; i < 3; i++)
        {
            using HttpResponseMessage before = await SendAsync(
                adminClient, conversationId, ClientKey(9300 + i), $"visible to everyone {i}");
            before.EnsureSuccessStatusCode();
        }

        using HttpResponseMessage addResponse = await adminClient.PostAsJsonAsync(
            new Uri($"/api/v1/conversations/{conversationId}/members", UriKind.Relative),
            new AddMemberRequest(newMemberId, Role: null));
        addResponse.EnsureSuccessStatusCode();

        using HttpClient newMemberClient = await AuthenticatedClientAsync(NewMember);

        MessagePageResponse page = await HistoryAsync(newMemberClient, conversationId);

        Assert.Equal(3, page.Items.Count);
    }

    private async Task<(Guid Admin, Guid Existing, Guid NewMember)> SeedThreeAsync(
        string admin, string existing, string newMember)
    {
        await using ChatDbContext context = CreateDbContext();

        Guid adminId = await TestData.SeedEmployeeAsync(context, admin);
        Guid existingId = await TestData.SeedEmployeeAsync(context, existing);
        Guid newMemberId = await TestData.SeedEmployeeAsync(context, newMember);

        return (adminId, existingId, newMemberId);
    }

    private static async Task<Guid> CreateGroupAsync(
        HttpClient adminClient, string name, IReadOnlyList<Guid> memberIds, string historyVisibility)
    {
        using HttpResponseMessage response = await adminClient.PostAsJsonAsync(
            new Uri("/api/v1/conversations", UriKind.Relative),
            new CreateConversationRequest("group", name, memberIds, historyVisibility));

        response.EnsureSuccessStatusCode();

        ConversationResponse conversation = await response.Content.ReadFromJsonAsync<ConversationResponse>()
            ?? throw new InvalidOperationException("Conversation creation returned an empty body.");

        return conversation.Id;
    }
}
