using System.Net;
using System.Net.Http.Headers;
using InternalChat.Infrastructure.Persistence;
using InternalChat.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.SignalR.Client;

namespace InternalChat.IntegrationTests.Authorization;

/// <summary>
/// T057 — contracts/signalr-hub.md: "JWT re-validated and the revocation set re-checked — a
/// long-lived connection MUST NOT outlive its token (D5)."
/// </summary>
/// <remarks>
/// <para>
/// The failure this guards against is subtle and common. SignalR's automatic reconnect reuses the
/// stored access token, and the negotiate step happily accepts whatever the handler last authorised.
/// If the reconnect path does not re-validate, an employee whose token expired at 09:05 keeps a
/// working connection indefinitely so long as the socket flaps occasionally — which is the opposite
/// of what a short token lifetime was chosen to achieve.
/// </para>
/// <para>
/// The tokens here are real and really expired: <see cref="StackFixture.IssueExpiredAccessTokenAsync"/>
/// narrows the realm's lifespan through the admin API rather than fabricating a token, so what is
/// being validated is Keycloak's signature and Keycloak's <c>exp</c>.
/// </para>
/// </remarks>
public sealed class ReconnectValidationTests : IntegrationTestBase, IAsyncLifetime
{
    private readonly ApiFactory _api;

    public ReconnectValidationTests(StackFixture stack)
        : base(stack) => _api = new ApiFactory(stack);

    public override async Task DisposeAsync()
    {
        await _api.DisposeAsync();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task A_connection_attempt_with_an_expired_token_is_refused()
    {
        const string Username = "khanh.dang";

        await using (ChatDbContext context = CreateDbContext())
        {
            await TestData.SeedEmployeeAsync(context, Username);
        }

        string expired = await Stack.IssueExpiredAccessTokenAsync(Username);

        await using HubConnection connection = HubClient.Create(_api, expired);

        await HubClient.RefusedAsUnauthorizedAsync(connection);
    }

    /// <summary>
    /// A reconnect is a fresh negotiate, so the same refusal must apply. Asserted separately
    /// because "we validate on connect" and "we validate on reconnect" are different code paths in
    /// most implementations, and D5 requires both.
    /// </summary>
    [Fact]
    public async Task A_reconnect_carrying_a_token_that_has_since_expired_is_refused()
    {
        const string Username = "lan.bui";

        await using (ChatDbContext context = CreateDbContext())
        {
            await TestData.SeedEmployeeAsync(context, Username);
        }

        string valid = await Stack.IssueAccessTokenAsync(Username);
        string expired = await Stack.IssueExpiredAccessTokenAsync(Username);

        // The token the client presents changes between the first connect and the reconnect,
        // which is what a real client does when its silent refresh has failed.
        string current = valid;

        await using HubConnection connection = HubClient.Create(_api, () => current);

        await connection.StartAsync();
        Assert.Equal(HubConnectionState.Connected, connection.State);

        await connection.StopAsync();

        current = expired;

        await HubClient.RefusedAsUnauthorizedAsync(connection);
    }

    /// <summary>
    /// The same rule over HTTP. Stated separately because the hub and the request pipeline are
    /// configured independently, and an expired token accepted on either one defeats the five-minute
    /// budget equally.
    /// </summary>
    [Fact]
    public async Task An_expired_token_is_refused_on_the_http_api()
    {
        const string Username = "minh.do";

        await using (ChatDbContext context = CreateDbContext())
        {
            await TestData.SeedEmployeeAsync(context, Username);
        }

        string expired = await Stack.IssueExpiredAccessTokenAsync(Username);

        using HttpClient client = _api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", expired);

        using HttpResponseMessage response = await client.GetAsync(new Uri("/api/v1/me", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The control: the same request with a live token succeeds. Without it, an API that refused
    /// everything would pass the assertion above.
    /// </summary>
    [Fact]
    public async Task A_live_token_is_accepted_on_the_http_api()
    {
        const string Username = "nga.ngo";

        await using (ChatDbContext context = CreateDbContext())
        {
            await TestData.SeedEmployeeAsync(context, Username);
        }

        string token = await Stack.IssueAccessTokenAsync(Username);

        using HttpClient client = _api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.GetAsync(new Uri("/api/v1/me", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
