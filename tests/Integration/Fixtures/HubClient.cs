using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;

namespace InternalChat.IntegrationTests.Fixtures;

/// <summary>
/// Opens a real SignalR connection to the hosted API.
/// </summary>
/// <remarks>
/// <para>
/// The transport is a genuine WebSocket handshake — <see cref="TestServer.CreateWebSocketClient"/>
/// negotiates and upgrades through the application's actual middleware pipeline — carried over the
/// in-memory transport rather than a TCP socket. Everything under test here is server-side:
/// whether the hub filter refuses a connection, whether the sweep aborts one, and whether the
/// client observes the closure. All of that is identical either way.
/// </para>
/// <para>
/// An earlier version ran a second Kestrel host on a loopback port to get a physical socket. It
/// was abandoned: building the same host builder twice does not reliably produce a configured
/// application under minimal hosting, and the failure presented as the hub returning 404 — which
/// looks exactly like a routing mistake. Physical-socket behaviour is covered by the Playwright
/// run against the Compose stack (T076), where it is real rather than simulated.
/// </para>
/// </remarks>
public static class HubClient
{
    /// <summary>Builds a hub connection authenticated with the given access token.</summary>
    public static HubConnection Create(ApiFactory api, string accessToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        return Create(api, () => accessToken);
    }

    /// <summary>
    /// Builds a hub connection whose token is read fresh on every connect.
    /// </summary>
    /// <remarks>
    /// This is how a real client behaves — the provider returns whatever the silent refresh last
    /// obtained — and it is the only way to test a reconnect that carries a different token from
    /// the one the first connect used.
    /// </remarks>
    public static HubConnection Create(ApiFactory api, Func<string> accessTokenProvider)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(accessTokenProvider);

        TestServer server = api.Server;

        return new HubConnectionBuilder()
            .WithUrl(
                new Uri(server.BaseAddress, "hubs/chat"),
                options =>
                {
                    // Negotiate and long-polling requests go through the test handler.
                    options.HttpMessageHandlerFactory = _ => server.CreateHandler();

                    // The upgrade itself. Without this the client would try to open a real socket
                    // to the fake base address and fail with a connection error that has nothing
                    // to do with the behaviour under test.
                    options.WebSocketFactory = async (context, cancellationToken) =>
                    {
                        WebSocketClient client = server.CreateWebSocketClient();

                        // The browser WebSocket API cannot set headers, which is why the API
                        // accepts `access_token` from the query string on the hub path — and the
                        // client already appends it. Setting the header as well exercises the
                        // ordinary path; the query fallback is what a real browser uses.
                        client.ConfigureRequest = request =>
                            request.Headers["Authorization"] = $"Bearer {accessTokenProvider()}";

                        UriBuilder uri = new(context.Uri) { Scheme = Uri.UriSchemeHttp };

                        return await client.ConnectAsync(uri.Uri, cancellationToken);
                    };

                    options.AccessTokenProvider = () => Task.FromResult<string?>(accessTokenProvider());
                })
            .Build();
    }

    /// <summary>Asserts the connection was refused specifically because the token was not accepted.</summary>
    /// <remarks>
    /// <para>
    /// The obvious assertion — any exception from <see cref="HubConnection.StartAsync"/> — passes
    /// when the hub does not exist, when the address is wrong, and when the server has crashed.
    /// Every one of those is a green test for a broken system, and the first version of these tests
    /// demonstrated it: two passed against an API with no hub mapped at all.
    /// </para>
    /// <para>
    /// So the refusal is asserted by status code. Negotiate is an ordinary HTTP request, so a
    /// rejected token arrives as 401 and a missing hub as 404 — exactly the distinction that
    /// matters.
    /// </para>
    /// </remarks>
    public static async Task RefusedAsUnauthorizedAsync(HubConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        HttpRequestException exception =
            await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Equal(HubConnectionState.Disconnected, connection.State);
    }
}
