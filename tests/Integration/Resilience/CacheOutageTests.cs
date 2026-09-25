using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using InternalChat.Api.Contracts;
using InternalChat.IntegrationTests.Fixtures;
using Xunit;

namespace InternalChat.IntegrationTests.Resilience;

/// <summary>
/// T213 — Asserts a full cache-tier outage causes latency only, never data loss or an incorrect access decision.
/// </summary>
public sealed class CacheOutageTests : MessagingTestBase
{
    public CacheOutageTests(StackFixture stack) : base(stack)
    {
    }

    [Fact]
    public async Task Cache_outage_does_not_break_messaging_flow()
    {
        const string First = "an.nguyen";
        const string Second = "binh.tran";

        // 1. Seed two employees and a direct conversation
        await using (var context = Stack.CreateDbContext())
        {
            (Guid firstId, Guid secondId) = (
                await TestData.SeedEmployeeAsync(context, First),
                await TestData.SeedEmployeeAsync(context, Second)
            );
            Guid conversationId = await SeedDirectConversationAsync(firstId, secondId);

            using HttpClient firstClient = await AuthenticatedClientAsync(First);
            
            // 2. Initial healthy request to populate cache
            HttpResponseMessage healthyResponse = await firstClient.GetAsync($"/api/v1/conversations/{conversationId}");
            Assert.Equal(HttpStatusCode.OK, healthyResponse.StatusCode);

            // 3. Take down the cache tier
            await Stack.StopRedisAsync();

            try
            {
                // 4. Act: Attempt messaging operations while Redis is down
                
                // Should succeed despite cache being unreachable
                HttpResponseMessage readResponse = await firstClient.GetAsync($"/api/v1/conversations/{conversationId}");
                if (!readResponse.IsSuccessStatusCode)
                {
                    string error = await readResponse.Content.ReadAsStringAsync();
                    throw new InvalidOperationException($"Expected OK, but got {readResponse.StatusCode}: {error}");
                }
                Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);

                // Send a message
                using HttpResponseMessage sendResponse = await SendAsync(firstClient, conversationId, ClientKey(1), "Message during cache outage");
                Assert.Equal(HttpStatusCode.Created, sendResponse.StatusCode);
                
                var sentMsg = await ReadMessageAsync(sendResponse);

                // Fetch history
                var history = await HistoryAsync(firstClient, conversationId);
                Assert.Contains(history.Items, m => m.Id == sentMsg.Id);
            }
            finally
            {
                // 5. Restore cache tier
                await Stack.StartRedisAsync();
            }
        }
    }
}
