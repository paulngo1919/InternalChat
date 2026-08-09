using System.Diagnostics;
using InternalChat.Application.Abstractions;
using InternalChat.Infrastructure.Persistence;
using InternalChat.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace InternalChat.IntegrationTests.Authorization;

/// <summary>
/// T056 — FR-003 and SC-018: deactivating an employee stops an <b>already-open</b> connection
/// within five minutes.
/// </summary>
/// <remarks>
/// <para>
/// quickstart.md V1 says of this scenario: "Step 4 is the one that fails in most implementations.
/// An open connection that outlives its token is the default behaviour, not the exception." That is
/// exactly right, and it is why these tests open a genuine WebSocket — handshake, upgrade, and all,
/// through the application's real middleware pipeline — and then do nothing with it. The client
/// sends no request between the revocation and the closure, because a client that invoked something
/// would be caught by the hub filter and would prove nothing about the idle case.
/// </para>
/// <para>
/// The sweep interval is compressed to one second here. The production default is thirty, and the
/// budget is five minutes; a test that actually waited five minutes would be deleted by the third
/// person who ran the suite. What is being asserted is that a periodic re-check exists and closes
/// the connection — not the specific number, which is configuration.
/// </para>
/// </remarks>
public sealed class RevocationTests : IntegrationTestBase, IAsyncLifetime
{
    /// <summary>Generous relative to the one-second sweep, tight relative to the five-minute budget.</summary>
    private static readonly TimeSpan ClosureBudget = TimeSpan.FromSeconds(30);

    private readonly ApiFactory _api;

    public RevocationTests(StackFixture stack)
        : base(stack)
    {
        _api = new ApiFactory(
            stack,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Keycloak:RevocationSweepSeconds"] = "1",
            });
    }

    public override async Task DisposeAsync()
    {
        await _api.DisposeAsync();
        await base.DisposeAsync();
    }

    /// <summary>
    /// The scenario in as many words: connection open first, revocation second, closure without the
    /// client having done anything.
    /// </summary>
    [Fact]
    public async Task Deactivating_an_employee_closes_an_already_open_connection()
    {
        const string Username = "an.nguyen";

        await using (ChatDbContext context = CreateDbContext())
        {
            await TestData.SeedEmployeeAsync(context, Username);
        }

        string token = await Stack.IssueAccessTokenAsync(Username);

        await using HubConnection connection = BuildConnection(token);

        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        await connection.StartAsync();
        Assert.Equal(HubConnectionState.Connected, connection.State);

        // Deactivation, exactly as directory sync performs it: the revocation set is written from
        // the employee's external subject, because the deactivation path has no way to enumerate
        // the sessions it needs to end.
        IRevocationStore revocations = _api.Services.GetRequiredService<IRevocationStore>();
        await revocations
            .RevokeSubjectAsync(TestData.SubjectFor(Username), TimeSpan.FromMinutes(5));

        Stopwatch elapsed = Stopwatch.StartNew();

        Task completed = await Task
            .WhenAny(closed.Task, Task.Delay(ClosureBudget));

        Assert.True(
            ReferenceEquals(completed, closed.Task),
            $"""
            The connection was still open {ClosureBudget.TotalSeconds:F0}s after the employee was
            revoked, with the sweep interval set to 1s.

            FR-003 and SC-018 require access to end within five minutes INCLUDING for a connection
            that was already established. A hub filter alone does not achieve this: it only runs
            when the client invokes something, and an idle connection invokes nothing. Something has
            to re-check open connections on a timer.
            """);

        Assert.Equal(HubConnectionState.Disconnected, connection.State);
        Assert.True(elapsed.Elapsed < TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// The control. Without this, an implementation that closed every connection on every sweep
    /// would pass the test above and look correct.
    /// </summary>
    [Fact]
    public async Task A_connection_for_an_employee_who_was_not_revoked_survives_the_sweep()
    {
        const string Username = "binh.tran";

        await using (ChatDbContext context = CreateDbContext())
        {
            await TestData.SeedEmployeeAsync(context, Username);
        }

        string token = await Stack.IssueAccessTokenAsync(Username);

        await using HubConnection connection = BuildConnection(token);
        await connection.StartAsync();

        // Comfortably more than several sweeps at one second each.
        await Task.Delay(TimeSpan.FromSeconds(5));

        Assert.Equal(HubConnectionState.Connected, connection.State);
    }

    /// <summary>
    /// Revoking one employee must not disturb another's connection. Keying the revocation check
    /// wrongly — sweeping by connection age, or matching a prefix — would show up here and nowhere
    /// else.
    /// </summary>
    [Fact]
    public async Task Revoking_one_employee_leaves_another_connected()
    {
        const string Revoked = "chi.le";
        const string Untouched = "dung.pham";

        await using (ChatDbContext context = CreateDbContext())
        {
            await TestData.SeedEmployeeAsync(context, Revoked);
            await TestData.SeedEmployeeAsync(context, Untouched);
        }

        await using HubConnection revokedConnection =
            BuildConnection(await Stack.IssueAccessTokenAsync(Revoked));
        await using HubConnection survivingConnection =
            BuildConnection(await Stack.IssueAccessTokenAsync(Untouched));

        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        revokedConnection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        await revokedConnection.StartAsync();
        await survivingConnection.StartAsync();

        IRevocationStore revocations = _api.Services.GetRequiredService<IRevocationStore>();
        await revocations
            .RevokeSubjectAsync(TestData.SubjectFor(Revoked), TimeSpan.FromMinutes(5));

        Task completed = await Task
            .WhenAny(closed.Task, Task.Delay(ClosureBudget));

        Assert.True(ReferenceEquals(completed, closed.Task), "The revoked employee's connection stayed open.");
        Assert.Equal(HubConnectionState.Connected, survivingConnection.State);
    }

    /// <summary>
    /// A revoked token must also be refused at the door, not only swept away later. Otherwise a
    /// revoked employee could reconnect and hold a working connection for up to one sweep interval,
    /// repeatedly.
    /// </summary>
    [Fact]
    public async Task A_revoked_subject_cannot_open_a_new_connection()
    {
        const string Username = "hai.vo";

        await using (ChatDbContext context = CreateDbContext())
        {
            await TestData.SeedEmployeeAsync(context, Username);
        }

        string token = await Stack.IssueAccessTokenAsync(Username);

        IRevocationStore revocations = _api.Services.GetRequiredService<IRevocationStore>();
        await revocations
            .RevokeSubjectAsync(TestData.SubjectFor(Username), TimeSpan.FromMinutes(5));

        await using HubConnection connection = BuildConnection(token);

        await HubClient.RefusedAsUnauthorizedAsync(connection);
    }

    private HubConnection BuildConnection(string accessToken) => HubClient.Create(_api, accessToken);
}
