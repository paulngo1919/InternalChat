using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

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

            ["RabbitMq:ConnectionString"] = _stack.RabbitMqConnectionString,
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
    }

    /// <summary>Formats a value for the configuration providers, which are string-only.</summary>
    public static string Setting(int value) => value.ToString(CultureInfo.InvariantCulture);
}
