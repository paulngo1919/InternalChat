using System.Reflection;
using System.Text.Json;
using InternalChat.Api.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace InternalChat.ContractTests;

/// <summary>
/// T180 — the meeting endpoints against <c>contracts/openapi.yaml</c>.
/// </summary>
/// <remarks>
/// <para>
/// One test here is not about the contract at all, and it is the most important: the webhook
/// receiver must be <b>absent</b> from the routing table's documented surface and present in the
/// application. It is authenticated by signature rather than by a user token, so publishing it in
/// the client contract would invite a generated client to call an endpoint that will refuse it —
/// and, worse, suggest it is part of the API somebody should be able to use.
/// </para>
/// </remarks>
public sealed class MeetingContractTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly WebApplicationFactory<Program> _factory;
    private readonly OpenApiContract _contract = OpenApiContract.Load();

    public MeetingContractTests(WebApplicationFactory<Program> factory) => _factory = factory;

    /// <summary>The meeting operations this task covers.</summary>
    public static TheoryData<string, string> DocumentedOperations() => new()
    {
        { "/conversations/{conversationId}/meetings", "POST" },
        { "/meetings/{meetingId}/token", "POST" },
    };

    [Theory]
    [MemberData(nameof(DocumentedOperations))]
    public void Every_documented_meeting_operation_is_mapped(string path, string method)
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
    public void Meeting_serves_every_documented_property()
    {
        IReadOnlyCollection<string> documented = _contract.PropertiesOf("Meeting");
        IReadOnlyCollection<string> serialized = SerializedPropertyNames<MeetingResponse>();

        List<string> missing = [.. documented.Where(d => !serialized.Contains(d, StringComparer.Ordinal))];

        Assert.True(
            missing.Count == 0,
            $"Meeting does not serialize {string.Join(", ", missing)}. Serialized: {string.Join(", ", serialized.Order())}");
    }

    [Fact]
    public void A_meeting_reports_the_participant_ceiling_rather_than_assuming_it()
    {
        Assert.Contains("maxParticipants", _contract.PropertiesOf("Meeting"));
        Assert.Contains("maxParticipants", SerializedPropertyNames<MeetingResponse>());

        // Served rather than hard-coded in the client, so raising the limit does not need a
        // frontend deployment before the UI stops saying "25".
        MeetingResponse meeting = new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            EndedAt: null,
            ParticipantCount: 3,
            MaxParticipants: Domain.Meetings.Meeting.MaximumParticipants);

        Assert.Equal(25, meeting.MaxParticipants);
    }

    [Fact]
    public void A_live_meeting_can_be_represented_because_the_end_time_is_nullable()
    {
        Assert.True(IsNullableValue(typeof(MeetingResponse), nameof(MeetingResponse.EndedAt)));

        MeetingResponse live = new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            EndedAt: null,
            ParticipantCount: 1,
            MaxParticipants: 25);

        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(live, SerializerOptions));

        // A non-nullable endedAt would make the ordinary state — a meeting in progress —
        // unrepresentable, so the API would have to invent a time for a call that is still running.
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("endedAt").ValueKind);
    }

    [Fact]
    public void A_meeting_token_carries_its_expiry()
    {
        IReadOnlyCollection<string> serialized = SerializedPropertyNames<MeetingTokenResponse>();

        Assert.Contains("token", serialized);
        Assert.Contains("mediaServerUrl", serialized);

        // The token is a real bearer capability — unlike an attachment URL, the media server cannot
        // re-check it. A client that knows when it expires renews rather than failing to connect.
        Assert.Contains("expiresAt", serialized);
    }

    /// <summary>
    /// The webhook receiver is reachable but undocumented.
    /// </summary>
    /// <remarks>
    /// Both halves matter. Present, because LiveKit must be able to post to it. Undocumented,
    /// because it is authenticated by signature rather than by a user token — publishing it would
    /// put an endpoint in the client contract that refuses every client.
    /// </remarks>
    [Fact]
    public void The_livekit_webhook_is_mapped_but_not_part_of_the_client_contract()
    {
        Assert.DoesNotContain("/webhooks/livekit", _contract.Paths);

        bool mapped = Endpoints().Any(endpoint =>
            string.Equals(RoutePatterns.Of(endpoint), "/webhooks/livekit", StringComparison.OrdinalIgnoreCase));

        Assert.True(mapped, "The LiveKit webhook receiver is not mapped; meetings would never end.");
    }

    /// <summary>
    /// The webhook sits outside the versioned API prefix.
    /// </summary>
    /// <remarks>
    /// Deliberate: <c>/api/v1</c> is the client contract's surface and carries its versioning
    /// promise. A third-party callback has no business inside it, and putting it there would mean
    /// a future <c>/api/v2</c> either moved LiveKit's configured URL or left a v1 path alive for it.
    /// </remarks>
    [Fact]
    public void The_webhook_is_outside_the_versioned_api_prefix()
    {
        Endpoint webhook = Assert.Single(
            Endpoints(),
            endpoint => RoutePatterns.Of(endpoint)
                .Contains("webhooks/livekit", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain("/api/v1", RoutePatterns.Of(webhook), StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<Endpoint> Endpoints() =>
        _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

    private static IReadOnlyCollection<string> SerializedPropertyNames<T>()
    {
        object instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(T));
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize((T)instance, SerializerOptions));

        return [.. document.RootElement.EnumerateObject().Select(p => p.Name)];
    }

    private static bool IsNullableValue(Type type, string propertyName)
    {
        PropertyInfo property = type.GetProperty(propertyName)!;

        return Nullable.GetUnderlyingType(property.PropertyType) is not null;
    }
}
