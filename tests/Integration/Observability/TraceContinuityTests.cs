using InternalChat.Application.Abstractions;
using System.Diagnostics;
using System.Text;
using InternalChat.Application.Telemetry;
using InternalChat.Domain.Common;
using InternalChat.Infrastructure.Messaging;
using InternalChat.Infrastructure.Persistence;
using InternalChat.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace InternalChat.IntegrationTests.Observability;

/// <summary>
/// Verifies that a trace survives the asynchronous hop — T045.
/// </summary>
/// <remarks>
/// <para>
/// The constitution requires W3C trace context propagated across HTTP, SignalR, and RabbitMQ. The
/// first two come free from ASP.NET Core instrumentation; the broker hop does not, and it is the
/// one that matters most. A request that sends a message returns 200 long before the notification
/// is delivered, and without continuity the two halves are separate traces with nothing linking
/// them. "The send succeeded but nobody was notified" then takes a log search instead of a click.
/// </para>
/// <para>
/// Carrying the header is not the same as joining the trace, and it is the difference these tests
/// exist to catch: the header can be present and correct while the consumer still opens a root
/// span, which produces a trace that looks fine in isolation and connects to nothing.
/// </para>
/// </remarks>
public sealed class TraceContinuityTests : IntegrationTestBase, IAsyncLifetime
{
    private const string Queue = "notifications.fanout";

    private RabbitMqConnectionProvider _connectionProvider = null!;
    private RabbitMqOptions _options = null!;
    private ServiceProvider _services = null!;

    public TraceContinuityTests(StackFixture stack)
        : base(stack)
    {
    }

    private sealed record ProbeEvent(Guid EventId, DateTimeOffset OccurredAt)
        : DomainEvent(EventId, OccurredAt)
    {
        public override string EventType => "chat.message.sent.direct";
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

        _connectionProvider = new RabbitMqConnectionProvider(Options.Create(_options));

        ServiceCollection services = new();
        string connectionString = Stack.PostgresConnectionString;
        services.AddDbContext<ChatDbContext>(o =>
            ChatDbContext.ConfigureNpgsql((DbContextOptionsBuilder<ChatDbContext>)o, connectionString));
        _services = services.BuildServiceProvider();

        IChannel channel = await _connectionProvider.CreateChannelAsync();
        await using (channel.ConfigureAwait(false))
        {
            await ChatTopology.DeclareAsync(channel);
            await channel.QueuePurgeAsync(Queue);
            await channel.QueuePurgeAsync("realtime.fanout");
        }
    }

    public override async Task DisposeAsync()
    {
        if (_connectionProvider is not null)
        {
            await _connectionProvider.DisposeAsync();
        }

        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    /// <summary>
    /// Subscribes to the application's activity source and records every span it starts.
    /// </summary>
    /// <remarks>
    /// Without a listener that returns <see cref="ActivitySamplingResult.AllData"/>,
    /// <c>StartActivity</c> returns null and every assertion here would pass vacuously against a
    /// span that was never created. The listener IS the test's fixture, not a detail.
    /// </remarks>
    private static (ActivityListener Listener, List<Activity> Recorded) Listen()
    {
        List<Activity> recorded = [];

        ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == ChatTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = recorded.Add,
        };

        ActivitySource.AddActivityListener(listener);
        return (listener, recorded);
    }

    [Fact]
    public async Task Consuming_a_message_continues_the_trace_that_produced_it()
    {
        (ActivityListener listener, List<Activity> recorded) = Listen();
        using (listener)
        {
            string traceParent;
            ActivityTraceId requestTraceId;
            ActivitySpanId requestSpanId;

            // Stands in for the HTTP request that sent the message.
            using (Activity request = new ActivitySource(ChatTelemetry.ActivitySourceName)
                .StartActivity("POST /conversations/{id}/messages", ActivityKind.Server)!)
            {
                Assert.NotNull(request);
                traceParent = request.Id!;
                requestTraceId = request.TraceId;
                requestSpanId = request.SpanId;
            }

            // Load-bearing, and not obviously so. The Worker receives deliveries on a broker
            // callback with no ambient activity, so the header is the ONLY link back to the
            // request. Leaving Activity.Current set — as it would be if this test simply called
            // the consumer inside the request's scope — makes the assertions below pass whether
            // the header is parsed or ignored, which is a test that proves nothing.
            Activity.Current = null;

            await using ConsumerHost host = CreateHost();
            IChannel channel = await _connectionProvider.CreateChannelAsync();

            await using (channel.ConfigureAwait(false))
            {
                await host.HandleDeliveryAsync(
                    channel, new NoOpConsumer(), Deliver(Guid.CreateVersion7(), traceParent));
            }

            Activity consume = Assert.Single(recorded, a => a.Kind == ActivityKind.Consumer);

            // Same trace: this is what makes the two halves one timeline in Jaeger.
            Assert.Equal(requestTraceId, consume.TraceId);

            // And specifically a CHILD of the request, not a sibling that merely shares the trace
            // id. Parentage is what orders them; without it the viewer cannot say which caused
            // which.
            Assert.Equal(requestSpanId, consume.ParentSpanId);
        }
    }

    [Fact]
    public async Task Message_without_trace_context_is_still_consumed()
    {
        (ActivityListener listener, List<Activity> recorded) = Listen();
        using (listener)
        {
            NoOpConsumer consumer = new();

            await using ConsumerHost host = CreateHost();
            IChannel channel = await _connectionProvider.CreateChannelAsync();

            await using (channel.ConfigureAwait(false))
            {
                await host.HandleDeliveryAsync(channel, consumer, Deliver(Guid.CreateVersion7(), traceParent: null));
            }

            // Telemetry is not allowed to be load-bearing. A message published before this code
            // existed, or by a tool that does not set the header, must still be handled — degraded
            // observability, never a dropped message.
            Assert.Equal(1, consumer.Invocations);
            Assert.Single(recorded, a => a.Kind == ActivityKind.Consumer);
        }
    }

    [Fact]
    public async Task Dispatched_outbox_row_carries_the_trace_of_the_request_that_wrote_it()
    {
        (ActivityListener listener, List<Activity> recorded) = Listen();
        using (listener)
        {
            ActivityTraceId requestTraceId;

            using (Activity request = new ActivitySource(ChatTelemetry.ActivitySourceName)
                .StartActivity("POST /conversations/{id}/messages", ActivityKind.Server)!)
            {
                requestTraceId = request.TraceId;

                // Written inside the request, so OutboxEventPublisher captures Activity.Current.
                await using ChatDbContext write = CreateDbContext();
                OutboxEventPublisher publisher = new(write);
                await publisher.PublishAsync(new ProbeEvent(Guid.CreateVersion7(), DateTimeOffset.UtcNow));
                await write.SaveChangesAsync();
            }

            // Same reason as the consumer test: without this the dispatcher would inherit the
            // request's context ambiently and the assertions would hold even if row.TraceParent
            // were never read.
            Activity.Current = null;

            // Dispatch happens in the Worker, minutes later, with no ambient activity at all —
            // which is exactly why the trace id has to come off the row rather than off the
            // dispatcher's own context.
            await using ChatDbContext dispatchContext = CreateDbContext();
            OutboxDispatcher dispatcher = new(
                dispatchContext,
                _connectionProvider,
                Options.Create(_options),
                NullLogger<OutboxDispatcher>.Instance);

            Assert.Equal(1, await dispatcher.DispatchBatchAsync());

            Activity publish = Assert.Single(recorded, a => a.Kind == ActivityKind.Producer);
            Assert.Equal(requestTraceId, publish.TraceId);

            // And the header on the wire names the publish span, so the consumer nests under the
            // publish rather than jumping straight back to the request.
            IChannel channel = await _connectionProvider.CreateChannelAsync();
            await using (channel.ConfigureAwait(false))
            {
                BasicGetResult? delivered = await WaitForMessageAsync(channel, Queue, TimeSpan.FromSeconds(10));

                Assert.NotNull(delivered);
                Assert.Equal(publish.Id, ReadTraceParent(delivered.BasicProperties));
            }
        }
    }

    // -------------------------------------------------------------------------------------------

    private ConsumerHost CreateHost() => new(
        _connectionProvider,
        _services.GetRequiredService<IServiceScopeFactory>(),
        Options.Create(_options),
        NullLogger<ConsumerHost>.Instance);

    private sealed class NoOpConsumer : IMessageConsumer
    {
        private int _invocations;

        public int Invocations => _invocations;

        public string QueueName => Queue;

        public Task HandleAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocations);
            return Task.CompletedTask;
        }
    }

    private static BasicDeliverEventArgs Deliver(Guid messageId, string? traceParent)
    {
        Dictionary<string, object?> headers = new(StringComparer.Ordinal)
        {
            [ChatTopology.AttemptHeader] = 1,
        };

        if (traceParent is not null)
        {
            // RabbitMQ delivers header strings as byte arrays, which is the shape ReadEnvelope
            // has to cope with. Writing a plain string here would test a case that never occurs.
            headers["traceparent"] = Encoding.UTF8.GetBytes(traceParent);
        }

        BasicProperties properties = new()
        {
            MessageId = messageId.ToString(),
            Type = "chat.message.sent.v1",
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            Headers = headers,
        };

        return new BasicDeliverEventArgs(
            consumerTag: "test",
            deliveryTag: 0,
            redelivered: false,
            exchange: ChatTopology.EventsExchange,
            routingKey: "chat.message.sent.v1",
            properties: properties,
            body: Encoding.UTF8.GetBytes("""{"conversationId":"00000000-0000-0000-0000-000000000001"}"""),
            cancellationToken: CancellationToken.None);
    }

    private static string? ReadTraceParent(IReadOnlyBasicProperties properties)
    {
        if (properties.Headers?.TryGetValue("traceparent", out object? raw) != true)
        {
            return null;
        }

        return raw switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            string text => text,
            _ => null,
        };
    }

    private static async Task<BasicGetResult?> WaitForMessageAsync(
        IChannel channel,
        string queue,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            BasicGetResult? result = await channel.BasicGetAsync(queue, autoAck: true);

            if (result is not null)
            {
                return result;
            }

            await Task.Delay(50);
        }

        return null;
    }
}
