// Composition root for the Worker host.
//
// Constitution Principle I: this file is the ONLY place in this project permitted to name an
// Infrastructure type. tests/Architecture/CompositionRootTests.cs fails the build otherwise.
//
// Constitution Principle V: this host runs at a single replica. Scheduled jobs and queue
// consumers live here rather than inside the API so they execute exactly once regardless of
// how many API instances are running.

using InternalChat.Application;
using InternalChat.Application.Abstractions;
using InternalChat.Infrastructure;
using InternalChat.Worker.Consumers;
using InternalChat.Worker.Observability;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddHttpClient();

// Registered before anything else so a failure while starting a consumer or job is itself
// traced and logged through the same pipeline rather than only reaching stdout.
builder.Services.AddChatObservability(builder.Configuration, serviceName: "internalchat-worker");

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// Consumers are registered against the Application-layer port. The host below resolves them all
// and hands each to the Infrastructure consumer host, which owns acknowledgement, idempotency,
// bounded retry, and dead-lettering — so a consumer never implements any of those itself.
// Registered twice on purpose: once against the port, so the queue-consumer service can enumerate
// which queues to consume, and once as the concrete type, so ConsumerHost can resolve it from the
// per-message scope and share that scope's transaction.
builder.Services.AddScoped<DirectorySyncConsumer>();
builder.Services.AddScoped<IMessageConsumer>(sp => sp.GetRequiredService<DirectorySyncConsumer>());

builder.Services.AddHostedService<InternalChat.Infrastructure.Messaging.QueueConsumerService>();

// Registered in later phases:
//   builder.Services.AddHostedService<OutboxDispatcher>();          // T037 wiring
//   builder.Services.AddHostedService<PartitionMaintenanceJob>();   // T089
//   builder.Services.AddHostedService<RetentionSweepJob>();         // T206

var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
