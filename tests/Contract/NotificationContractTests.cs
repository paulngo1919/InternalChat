using System.Text.Json;
using InternalChat.Api.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace InternalChat.ContractTests;

/// <summary>
/// T125 — the notification endpoints against <c>contracts/openapi.yaml</c> (US4).
/// </summary>
/// <remarks>
/// Includes <c>/conversations/{conversationId}/mute</c>, which this phase added to the document
/// alongside the endpoint — see <c>MuteConversation</c>'s remarks for why the path did not exist
/// before, the same kind of gap T080 and T088 record for earlier stories.
/// </remarks>
public sealed class NotificationContractTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly WebApplicationFactory<Program> _factory;
    private readonly OpenApiContract _contract = OpenApiContract.Load();

    public NotificationContractTests(WebApplicationFactory<Program> factory) => _factory = factory;

    /// <summary>The notification operations this task covers.</summary>
    public static TheoryData<string, string> DocumentedOperations() => new()
    {
        { "/conversations/{conversationId}/read-state", "PUT" },
        { "/conversations/{conversationId}/mute", "PUT" },
        { "/notifications/preferences", "GET" },
        { "/notifications/preferences", "PUT" },
        { "/notifications/subscriptions", "POST" },
        { "/notifications/vapid-public-key", "GET" },
    };

    [Theory]
    [MemberData(nameof(DocumentedOperations))]
    public void Every_documented_notification_operation_is_mapped(string path, string method)
    {
        Assert.Contains(path, _contract.Paths);
        Assert.Contains(method, _contract.MethodsFor(path));

        string expected = _contract.BasePath + path;

        bool mapped = Endpoints().Any(endpoint =>
            string.Equals(RoutePatterns.Of(endpoint), expected, StringComparison.OrdinalIgnoreCase)
            && RoutePatterns.MethodsOf(endpoint).Contains(method, StringComparer.OrdinalIgnoreCase));

        Assert.True(
            mapped,
            $"""
            The contract documents {method} {expected}, and no endpoint serves it.

            Mapped routes:
              {string.Join("\n  ", Endpoints().Select(e => $"{string.Join('|', RoutePatterns.MethodsOf(e))} {RoutePatterns.Of(e)}").Order())}
            """);
    }

    [Fact]
    public void ReadState_serves_every_documented_property()
    {
        IReadOnlyCollection<string> documented = _contract.PropertiesOf("ReadState");
        IReadOnlyCollection<string> serialized = SerializedPropertyNames(
            new ReadStateResponse(Guid.CreateVersion7(), 42, 3));

        Assert.NotEmpty(documented);

        List<string> missing = [.. documented.Where(d => !serialized.Contains(d, StringComparer.Ordinal))];

        Assert.True(
            missing.Count == 0,
            $"ReadState does not serialize {string.Join(", ", missing)}. Serialized: {string.Join(", ", serialized.Order())}");
    }

    [Fact]
    public void The_read_state_sequence_is_a_sixty_four_bit_integer()
    {
        Assert.Equal("integer", _contract.TypeOfProperty("ReadState", "lastReadSeq"));

        Assert.Equal(
            typeof(long),
            typeof(ReadStateResponse).GetProperty(nameof(ReadStateResponse.LastReadSeq))!.PropertyType);
    }

    [Fact]
    public void NotificationPreferences_serves_every_documented_property()
    {
        IReadOnlyCollection<string> documented = _contract.PropertiesOf("NotificationPreferences");
        IReadOnlyCollection<string> serialized = SerializedPropertyNames(
            new NotificationPreferencesResponse("18:00", "08:00", "UTC", 60));

        Assert.NotEmpty(documented);

        List<string> missing = [.. documented.Where(d => !serialized.Contains(d, StringComparer.Ordinal))];

        Assert.True(
            missing.Count == 0,
            $"NotificationPreferences does not serialize {string.Join(", ", missing)}. Serialized: {string.Join(", ", serialized.Order())}");
    }

    /// <summary>
    /// The do-not-disturb bounds serialize as <c>"HH:mm"</c>, matching the contract's example —
    /// not the default <see cref="TimeOnly"/> format, which a client parsing "HH:mm" cannot read.
    /// </summary>
    [Fact]
    public void Do_not_disturb_bounds_serialize_as_hour_and_minute()
    {
        JsonElement sample = Serialize(new NotificationPreferencesResponse("18:00", "08:00", "UTC", 60));

        Assert.Equal("18:00", sample.GetProperty("dndStart").GetString());
        Assert.Equal("08:00", sample.GetProperty("dndEnd").GetString());
    }

    [Fact]
    public void A_push_subscription_request_requires_the_client_keys()
    {
        Assert.Contains("p256dh", _contract.RequiredPropertiesOf("PushSubscriptionRequest"));
        Assert.Contains("auth", _contract.RequiredPropertiesOf("PushSubscriptionRequest"));
        Assert.Contains("endpoint", _contract.RequiredPropertiesOf("PushSubscriptionRequest"));

        IReadOnlyCollection<string> serialized = SerializedPropertyNames(
            new PushSubscriptionRequest("https://push.example/x", "p256dh-key", "auth-secret"));

        Assert.Contains("p256dh", serialized);
        Assert.Contains("auth", serialized);
        Assert.Contains("endpoint", serialized);
    }

    private static IReadOnlyCollection<string> SerializedPropertyNames<T>(T sample) =>
        [.. Serialize(sample).EnumerateObject().Select(p => p.Name)];

    private static JsonElement Serialize<T>(T sample)
    {
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(sample, SerializerOptions));
        return document.RootElement.Clone();
    }

    private IEnumerable<Endpoint> Endpoints() =>
        _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;
}
