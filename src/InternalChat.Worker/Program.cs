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

// T133 — decides who gets a push for a message (mute, DND, mention-only for groups).
builder.Services.AddScoped<InternalChat.Worker.Consumers.NotificationFanoutConsumer>();
builder.Services.AddScoped<IMessageConsumer>(
    sp => sp.GetRequiredService<InternalChat.Worker.Consumers.NotificationFanoutConsumer>());

// T152 — scan, promote, and record a verdict for every upload (FR-024). Registered in the Worker
// only: the API must never hold a path that can promote an object out of quarantine.
builder.Services.AddScoped<InternalChat.Worker.Consumers.AttachmentScanConsumer>();
builder.Services.AddScoped<IMessageConsumer>(
    sp => sp.GetRequiredService<InternalChat.Worker.Consumers.AttachmentScanConsumer>());

// T209 — processes export requests, writes JSON to MinIO, and records the outcome (FR-055).
builder.Services.AddScoped<InternalChat.Worker.Jobs.ExportJob>();
builder.Services.AddScoped<IMessageConsumer>(
    sp => sp.GetRequiredService<InternalChat.Worker.Jobs.ExportJob>());

builder.Services.AddHostedService<InternalChat.Infrastructure.Messaging.QueueConsumerService>();

// The outbox drain. Nothing reaches RabbitMQ without it — every real-time delivery, notification,
// and search index update in the platform is downstream of this loop, because the publisher only
// ever writes rows inside the use case's transaction (Principle VI). It was built and tested in
// T037 and hosted here in T089's pass, having until then written to a table nothing drained.
builder.Services.AddHostedService<InternalChat.Infrastructure.Messaging.OutboxDispatcherService>();

// 002 — wakes the drain above the moment a transaction commits outbox rows, via PostgreSQL
// NOTIFY. Without it the drain runs on its backstop poll only, and every message waits for it.
builder.Services.AddHostedService<InternalChat.Infrastructure.Messaging.OutboxNotificationListener>();

// Keeps monthly message partitions ahead of the clock (T089). Not housekeeping: an INSERT into a
// month with no partition fails outright rather than falling back to the parent, so a boundary
// crossed without a partition ready rejects every send on the platform.
builder.Services.AddHostedService<InternalChat.Worker.Jobs.PartitionMaintenanceJob>();

// T210 — sweeps orphaned objects and abandoned uploads.
builder.Services.AddHostedService<InternalChat.Worker.Jobs.OrphanReclaimJob>();

// Batches a returning employee's backlog into one summary rather than a burst of individual
// pushes (T135, FR-038). Reads the same per-employee cooldown key NotificationFanoutConsumer
// writes, so the two must never disagree about what that key means — see NotificationCooldown.
builder.Services.AddHostedService<InternalChat.Worker.Jobs.DigestJob>();

// T192 — the durable meeting history FR-051 requires. In the Worker rather than the API so an
// audit write never sits on the real-time path that delivers the join prompt.
builder.Services.AddScoped<InternalChat.Worker.Consumers.MeetingAuditConsumer>();
builder.Services.AddScoped<IMessageConsumer>(
    sp => sp.GetRequiredService<InternalChat.Worker.Consumers.MeetingAuditConsumer>());

// T155 — warns administrators well before attachment storage fills, so provisioning happens
// before uploads start being refused rather than after (FR-028).
builder.Services.AddHostedService<InternalChat.Worker.Jobs.StorageCapacityJob>();

// T206 — expires content past the retention window by dropping whole message partitions (FR-052).
// Daily rather than monthly: a monthly schedule has one chance to fire, and a Worker restarting at
// that moment misses the window silently.
builder.Services.Configure<InternalChat.Worker.Jobs.RetentionOptions>(
    builder.Configuration.GetSection(InternalChat.Worker.Jobs.RetentionOptions.SectionName));
builder.Services.AddHostedService<InternalChat.Worker.Jobs.RetentionSweepJob>();

// Registered in later phases:


var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
