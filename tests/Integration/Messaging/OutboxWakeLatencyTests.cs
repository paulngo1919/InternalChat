using System.Collections.Concurrent;
using System.Diagnostics;
using InternalChat.Application;
using InternalChat.Domain.Common;
using InternalChat.Infrastructure.Messaging;
using InternalChat.Infrastructure.Persistence;
using InternalChat.IntegrationTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Xunit.Abstractions;

namespace InternalChat.IntegrationTests.Messaging;

/// <summary>
/// 002 T013/T014 — the regression tests for the reported delay (research R0, C1).
/// </summary>
/// <remarks>
/// <para>
/// Before 002 the dispatcher slept a fixed second whenever the outbox was empty, so a message
/// committed during that sleep waited for it to end — a mean of half a second on a normally loaded
/// platform, before the message had even reached the broker. These tests run the real dispatcher
/// loop and the real listener against real PostgreSQL and RabbitMQ, commit a row while the loop is
/// idle, and time how long it takes to arrive on <c>realtime.fanout</c>.
/// </para>
/// <para>
/// The backstop poll is set far longer than the budget, so only the <c>outbox_ready</c>
/// notification can explain a fast arrival. A test that left it at a second could pass by luck.
/// </para>
/// </remarks>
public sealed class OutboxWakeLatencyTests : IntegrationTestBase, IAsyncLifetime
{
    /// <summary>Commit to arrival on the queue, at p99. data-model §2; plan budget p99 100 ms end to end.</summary>
    private static readonly TimeSpan WakeBudget = TimeSpan.FromMilliseconds(50);

    private const int Trials = 20;

    private readonly ITestOutputHelper _output;
    private RabbitMqOptions _options = null!;

    public OutboxWakeLatencyTests(StackFixture stack, ITestOutputHelper output)
        : base(stack)
    {
        _output = output;
    }

    private sealed record ProbeEvent(Guid EventId, DateTimeOffset OccurredAt)
        : DomainEvent(EventId, OccurredAt)
    {
        public override string EventType => "chat.message.sent.v1";
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        Uri uri = new(Stack.RabbitMqConnectionString);
        _options = new RabbitMqOptions
        {
            Host = uri.Host,
            Port = uri.Port,
            User = "internalchat",
            Password = "internalchat",
            VHost = "/",
        };

        await using RabbitMqConnectionProvider provider = new(Options.Create(_options));
        IChannel channel = await provider.CreateChannelAsync();
        await using (channel.ConfigureAwait(false))
        {
            await ChatTopology.DeclareAsync(channel);

            foreach (QueueDefinition queue in ChatTopology.Queues)
            {
                await channel.QueuePurgeAsync(queue.Name);
            }
        }
    }

    [Fact]
    public async Task A_row_committed_while_the_dispatcher_is_idle_reaches_the_broker_within_the_wake_budget()
    {
        await using Harness harness = await Harness.StartAsync(Stack, _options, backstopPoll: TimeSpan.FromSeconds(30));

        List<TimeSpan> lags = [];

        for (int i = 0; i < Trials; i++)
        {
            // Idle long enough that the loop is genuinely waiting, not mid-pass.
            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(150, 400)));
            lags.Add(await harness.CommitAndAwaitDeliveryAsync(TimeSpan.FromSeconds(40)));
        }

        TimeSpan p50 = Percentile(lags, 50);
        TimeSpan p99 = Percentile(lags, 99);
        _output.WriteLine($"commit → realtime.fanout: p50 {p50.TotalMilliseconds:F1} ms, p99 {p99.TotalMilliseconds:F1} ms over {Trials}");

        Assert.True(
            p99 <= WakeBudget,
            $"p99 commit-to-broker was {p99.TotalMilliseconds:F1} ms against a {WakeBudget.TotalMilliseconds} ms budget. "
            + "The dispatcher is waiting on its poll rather than being woken by outbox_ready.");
    }

    [Fact]
    public async Task Without_the_listener_the_backstop_poll_still_drains_every_row()
    {
        // The pre-002 behaviour, kept as the safety net: no doorbell, one-second poll. Slower, never
        // lost (SC-010). The distribution it prints is also the baseline this feature removed.
        await using Harness harness = await Harness.StartAsync(
            Stack, _options, backstopPoll: TimeSpan.FromSeconds(1), startListener: false);

        List<TimeSpan> lags = [];

        for (int i = 0; i < 8; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000)));
            lags.Add(await harness.CommitAndAwaitDeliveryAsync(TimeSpan.FromSeconds(5)));
        }

        _output.WriteLine(
            $"poll only (pre-002): p50 {Percentile(lags, 50).TotalMilliseconds:F1} ms, "
            + $"max {lags.Max().TotalMilliseconds:F1} ms over {lags.Count}");

        Assert.All(lags, lag => Assert.True(lag <= TimeSpan.FromMilliseconds(1500), $"Backstop delivery took {lag.TotalMilliseconds:F1} ms."));
    }

    [Fact]
    public async Task A_dropped_listener_connection_reconnects_and_delivery_is_instant_again()
    {
        // Poll far longer than the test, so only a reconnected listener can explain fast delivery.
        await using Harness harness = await Harness.StartAsync(Stack, _options, backstopPoll: TimeSpan.FromSeconds(30));

        await harness.CommitAndAwaitDeliveryAsync(TimeSpan.FromSeconds(5));

        await TerminateListenerBackendsAsync();

        // Every commit is still delivered while the listener reconnects — the reconnect path wakes
        // the dispatcher once, so nothing waits for the 30 s poll.
        TimeSpan during = await harness.CommitAndAwaitDeliveryAsync(TimeSpan.FromSeconds(5));
        _output.WriteLine($"while reconnecting: {during.TotalMilliseconds:F1} ms");

        List<TimeSpan> after = [];

        for (int i = 0; i < 5; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(150, 400)));
            after.Add(await harness.CommitAndAwaitDeliveryAsync(TimeSpan.FromSeconds(5)));
        }

        _output.WriteLine($"after reconnect: max {after.Max().TotalMilliseconds:F1} ms");
        Assert.All(after, lag => Assert.True(lag <= WakeBudget, $"Delivery after reconnect took {lag.TotalMilliseconds:F1} ms."));
    }

    private async Task TerminateListenerBackendsAsync()
    {
        await using NpgsqlConnection admin = new(Stack.PostgresConnectionString);
        await admin.OpenAsync();

        await using NpgsqlCommand kill = new(
            $"""
            SELECT count(pg_terminate_backend(pid))
            FROM pg_stat_activity
            WHERE pid <> pg_backend_pid() AND query ILIKE 'LISTEN {OutboxNotificationListener.Channel}%'
            """,
            admin);

        long terminated = (long)(await kill.ExecuteScalarAsync())!;
        Assert.True(terminated >= 1, "No LISTEN backend was found to terminate; the listener was never connected.");
    }

    private static TimeSpan Percentile(List<TimeSpan> samples, int p)
    {
        List<TimeSpan> sorted = [.. samples.Order()];
        int rank = Math.Clamp((int)Math.Ceiling(p / 100.0 * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[rank];
    }

    /// <summary>
    /// The Worker's outbox path, assembled from the same types the Worker registers: dispatcher
    /// loop, listener, and wake signal, plus a raw consumer on <c>realtime.fanout</c> to time arrival.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly List<IHostedService> _hosted;
        private readonly StackFixture _stack;
        private readonly IChannel _consumeChannel;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<long>> _pending = new();

        private Harness(ServiceProvider services, List<IHostedService> hosted, StackFixture stack, IChannel consumeChannel)
        {
            _services = services;
            _hosted = hosted;
            _stack = stack;
            _consumeChannel = consumeChannel;
        }

        public static async Task<Harness> StartAsync(
            StackFixture stack,
            RabbitMqOptions broker,
            TimeSpan backstopPoll,
            bool startListener = true)
        {
            ServiceCollection services = new();

            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
            services.AddApplication();
            services.AddSingleton(Options.Create(new RabbitMqOptions
            {
                Host = broker.Host,
                Port = broker.Port,
                User = broker.User,
                Password = broker.Password,
                VHost = broker.VHost,
                IdlePollInterval = backstopPoll,
            }));
            services.AddSingleton(Options.Create(new OutboxListenerOptions
            {
                ConnectionString = stack.PostgresConnectionString,
            }));
            services.AddSingleton<IRabbitMqConnectionProvider, RabbitMqConnectionProvider>();
            services.AddSingleton<IOutboxWakeSignal, OutboxWakeSignal>();
            services.AddScoped(_ => stack.CreateDbContext());
            services.AddScoped<OutboxDispatcher>();
            services.AddSingleton<OutboxNotificationListener>();
            services.AddSingleton<OutboxDispatcherService>();

            ServiceProvider provider = services.BuildServiceProvider();

            IChannel channel = await provider.GetRequiredService<IRabbitMqConnectionProvider>().CreateChannelAsync();

            List<IHostedService> hosted = [];

            if (startListener)
            {
                hosted.Add(provider.GetRequiredService<OutboxNotificationListener>());
            }

            hosted.Add(provider.GetRequiredService<OutboxDispatcherService>());

            Harness harness = new(provider, hosted, stack, channel);

            AsyncEventingBasicConsumer consumer = new(channel);
            consumer.ReceivedAsync += (_, delivery) =>
            {
                long arrived = Stopwatch.GetTimestamp();

                if (delivery.BasicProperties.MessageId is { } id && harness._pending.TryRemove(id, out var waiter))
                {
                    waiter.TrySetResult(arrived);
                }

                return Task.CompletedTask;
            };

            await channel.BasicConsumeAsync("realtime.fanout", autoAck: true, consumer);

            foreach (IHostedService service in hosted)
            {
                await service.StartAsync(CancellationToken.None);
            }

            // Let the listener connect and the loop settle into its idle wait.
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            return harness;
        }

        /// <summary>Commits one outbox row and returns commit-to-arrival time.</summary>
        public async Task<TimeSpan> CommitAndAwaitDeliveryAsync(TimeSpan timeout)
        {
            Guid eventId = Guid.CreateVersion7();
            TaskCompletionSource<long> arrival = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[eventId.ToString()] = arrival;

            long committed;

            await using ChatDbContext context = _stack.CreateDbContext();
            await using (var transaction = await context.Database.BeginTransactionAsync())
            {
                await new OutboxEventPublisher(context).PublishAsync(new ProbeEvent(eventId, DateTimeOffset.UtcNow));
                await context.SaveChangesAsync();

                // Taken before COMMIT, not after: the notification is sent as the commit completes,
                // so a fast dispatcher can deliver before CommitAsync has even returned.
                committed = Stopwatch.GetTimestamp();
                await transaction.CommitAsync();
            }

            Task finished = await Task.WhenAny(arrival.Task, Task.Delay(timeout));
            Assert.True(ReferenceEquals(finished, arrival.Task), $"Outbox row {eventId} never reached realtime.fanout within {timeout}.");

            return Stopwatch.GetElapsedTime(committed, await arrival.Task);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (IHostedService service in Enumerable.Reverse(_hosted))
            {
                await service.StopAsync(CancellationToken.None);
            }

            await _consumeChannel.DisposeAsync();
            await _services.DisposeAsync();
        }
    }
}
