using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace InternalChat.IntegrationTests.Fixtures;

/// <summary>
/// Hosts the real API against the containerised stack.
/// </summary>
/// <remarks>
/// <para>
/// The whole application, not a slice of it: the same <c>Program.cs</c>, the same DI registrations,
/// the same middleware order. A test host assembled by hand would be a second composition root, and
/// the interesting authorization failures are precisely the ones that come from ordering and wiring
/// rather than from a method body.
/// </para>
/// <para>
/// Configuration is applied twice — as an in-memory source and as host settings. Under minimal
/// hosting, <c>WebApplication.CreateBuilder</c> has already produced its configuration by the time
/// <c>ConfigureAppConfiguration</c> sources land, so anything the application reads while
/// registering services would otherwise see <c>appsettings.json</c> and connect to the deployment's
/// hostnames. The application now reads those values lazily; <c>UseSetting</c> is the second line
/// of defence for anything that does not.
/// </para>
/// </remarks>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly StackFixture _stack;
    private readonly Dictionary<string, string?> _overrides;
    private readonly ConcurrentQueue<string> _logs = new();

    /// <summary>Creates a factory bound to the shared stack.</summary>
    /// <param name="stack">The containerised backing services.</param>
    /// <param name="overrides">
    /// Extra configuration, applied last. Used by tests that need to compress a production timer
    /// into something a test can wait for — the revocation sweep interval, for instance.
    /// </param>
    public ApiFactory(StackFixture stack, IDictionary<string, string?>? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(stack);

        _stack = stack;
        _overrides = overrides is null
            ? new Dictionary<string, string?>(StringComparer.Ordinal)
            : new Dictionary<string, string?>(overrides, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Testing");

        Uri broker = new(_stack.RabbitMqConnectionString);

        Dictionary<string, string?> settings = new(StringComparer.Ordinal)
        {
            ["ConnectionStrings:Postgres"] = _stack.PostgresConnectionString,
            ["ConnectionStrings:Redis"] = _stack.RedisConnectionString,

            // Namespaces every Redis key. IntegrationTestBase flushes the database between tests,
            // so this is about matching production's key shape, not isolation.
            ["Redis:Environment"] = "test",

            ["Keycloak:Authority"] = _stack.RealmAuthority,
            ["Keycloak:Audience"] = StackFixture.Audience,

            // The container speaks plain HTTP. Production does not, and the default stays true
            // there precisely so this has to be an explicit decision in one place.
            ["Keycloak:RequireHttpsMetadata"] = "false",

            // Decomposed, because RabbitMqOptions binds Host/Port/User/Password/VHost and has no
            // ConnectionString property. Setting one bound to nothing, so the API host quietly used
            // the defaults, failed to reach localhost:5672, and — because an unhandled
            // BackgroundService exception defaults to StopHost — shut the entire API down mid-test.
            // The symptom was a hub connection closing for no stated reason.
            ["RabbitMq:Host"] = broker.Host,
            ["RabbitMq:Port"] = Setting(broker.Port),
            ["RabbitMq:User"] = broker.UserInfo.Split(':')[0],
            ["RabbitMq:Password"] = broker.UserInfo.Split(':').ElementAtOrDefault(1) ?? string.Empty,
            ["RabbitMq:VHost"] = broker.AbsolutePath.Length > 1 ? broker.AbsolutePath[1..] : "/",

            // A real (but test-only, throwaway) VAPID key pair — not a production secret. The API
            // host itself never resolves IPushSender, but a test resolving a Worker-hosted consumer
            // (NotificationFanoutConsumer) through this factory's container needs WebPushSender to
            // construct without throwing, and any test that actually sends needs a genuine EC key
            // pair for the library's ES256 signing to succeed.
            ["WebPush:PublicKey"] = "BI0Oe7nD4cHRrX43CEr06HT2Ybda1KT9cykR9ylcEsZ0e4mjUtNonXPAha5GvwAJoivb1riEb3h64J6xgXr2SK4",
            ["WebPush:PrivateKey"] = "7dqawFdOUAN6OalUd9nIU6bB2QgNyTYk1GepYP8OTfI",
            ["WebPush:Subject"] = "mailto:test@internalchat.invalid",

            // Attachment storage (T150). The buckets are created by StackFixture, standing in for
            // the minio-init Compose service.
            ["Minio:Endpoint"] = _stack.MinioEndpoint,
            ["Minio:AccessKey"] = StackFixture.MinioAccessKey,
            ["Minio:SecretKey"] = StackFixture.MinioSecretKey,
            ["Minio:UseTls"] = "false",
            ["Minio:Bucket"] = StackFixture.AttachmentsBucket,
            ["Minio:QuarantineBucket"] = StackFixture.QuarantineBucket,

            // Small enough that a capacity test can fill it without uploading gigabytes, and large
            // enough that every other test stays far below the refusal threshold.
            ["Minio:CapacityBytes"] = Setting(64 * 1024 * 1024),

            // No reverse proxy in front of Kestrel here, so X-Accel-Redirect would be a header
            // nothing acts on and every retrieval test would assert against an empty 204. The
            // fallback streams the bytes instead — the authorization path is identical either way,
            // which is the part these tests are about.
            ["AttachmentDelivery:UseInternalRedirect"] = "false",

            // Meetings (T186). There is no LiveKit container in the integration stack, and that is
            // deliberate rather than a gap: the property most worth asserting is that the platform
            // stays fully functional when the media host is unreachable (constitution v1.2.0), and
            // the default state here IS unreachable. A test that needs a reachable one overrides
            // these, which is what MediaHostDegradationTests does in both directions.
            //
            // 127.0.0.1:1 is chosen because nothing listens on port 1 and a connection to it is
            // refused immediately — a black-hole address would make every probe wait for a timeout
            // and turn a fast assertion into a slow one.
            ["LiveKit:Url"] = "ws://127.0.0.1:1",
            ["LiveKit:ApiUrl"] = "http://127.0.0.1:1",
            ["LiveKit:ApiKey"] = "test-key",
            ["LiveKit:ApiSecret"] = "test-secret-not-a-real-one",

            // No cache between probes: a test that changes availability must see the change on its
            // next call rather than ten seconds later.
            ["LiveKit:AvailabilityCacheDuration"] = "00:00:00.001",
        };

        foreach ((string key, string? value) in _overrides)
        {
            settings[key] = value;
        }

        foreach ((string key, string? value) in settings)
        {
            if (value is not null)
            {
                builder.UseSetting(key, value);
            }
        }

        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));

        builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(_logs)));
    }

    /// <summary>
    /// Warnings and errors the hosted API logged, newest last.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exists because a failing integration test can otherwise only report the symptom. A hub
    /// connection that closes reports "expected Connected, actual Disconnected" and a request that
    /// fails reports a 500 with a trace id — while the exception that explains either is written to
    /// a logger nothing was reading.
    /// </para>
    /// <para>
    /// Warning and above only. Capturing Information would bury the one line that matters under
    /// EF's command logging.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> Logs => [.. _logs];

    /// <summary>The captured warnings and errors, formatted for a failure message.</summary>
    public string LogReport() =>
        _logs.IsEmpty ? "(the API logged no warnings or errors)" : string.Join("\n", _logs);

    /// <summary>Formats a value for the configuration providers, which are string-only.</summary>
    public static string Setting(int value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _sink;

        public CapturingLoggerProvider(ConcurrentQueue<string> sink) => _sink = sink;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _sink);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly string _category;
        private readonly ConcurrentQueue<string> _sink;

        public CapturingLogger(string category, ConcurrentQueue<string> sink)
        {
            _category = category;
            _sink = sink;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            ArgumentNullException.ThrowIfNull(formatter);

            string line = $"[{logLevel}] {_category}: {formatter(state, exception)}";

            if (exception is not null)
            {
                line += $"\n  {exception}";
            }

            _sink.Enqueue(line);
        }
    }
}
