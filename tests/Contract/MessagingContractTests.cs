using System.Reflection;
using System.Text.Json;
using InternalChat.Api.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace InternalChat.ContractTests;

/// <summary>
/// T080 — the conversation and message endpoints against <c>contracts/openapi.yaml</c>.
/// </summary>
/// <remarks>
/// <para>
/// Same shape as <see cref="DirectoryContractTests"/>: the document is parsed rather than restated,
/// and both halves that can drift are asserted — the routing table, so a generated client does not
/// call a 404, and the DTO wire format, so it does not read <c>null</c> out of a correctly-shaped
/// response.
/// </para>
/// <para>
/// The message DTO gets extra attention here beyond "the properties exist". <c>seq</c> must be a
/// 64-bit integer and <c>body</c> must be nullable, and both are the kind of mistake that produces
/// a working system for months and then a broken one — an <c>int32</c> <c>seq</c> is fine until a
/// busy conversation passes two billion messages, and a non-nullable <c>body</c> makes every
/// tombstone fail to deserialize the first time someone deletes a message.
/// </para>
/// </remarks>
public sealed class MessagingContractTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly WebApplicationFactory<Program> _factory;
    private readonly OpenApiContract _contract = OpenApiContract.Load();

    public MessagingContractTests(WebApplicationFactory<Program> factory) => _factory = factory;

    /// <summary>The conversation and message operations this task covers.</summary>
    public static TheoryData<string, string> DocumentedOperations() => new()
    {
        { "/conversations", "GET" },
        { "/conversations", "POST" },
        { "/conversations/{conversationId}", "GET" },
        { "/conversations/{conversationId}/messages", "GET" },
        { "/conversations/{conversationId}/messages", "POST" },
        { "/conversations/{conversationId}/messages/{messageId}", "PATCH" },
        { "/conversations/{conversationId}/messages/{messageId}", "DELETE" },
    };

    [Theory]
    [MemberData(nameof(DocumentedOperations))]
    public void Every_documented_messaging_operation_is_mapped(string path, string method)
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
    public void Message_carries_every_required_property()
    {
        AssertSatisfies<MessageResponse>("Message");
    }

    [Fact]
    public void Conversation_carries_every_required_property()
    {
        AssertSatisfies<ConversationResponse>("Conversation");
    }

    /// <summary>
    /// Every property the contract documents on <c>Message</c> is actually served.
    /// </summary>
    /// <remarks>
    /// Stricter than the required-only check because the optional ones here are not decoration:
    /// <c>editedAt</c> and <c>deletedAt</c> are how a client tells an edited message from a
    /// tombstone, and a response omitting them renders both as ordinary messages.
    /// </remarks>
    [Fact]
    public void Message_serves_every_documented_property()
    {
        IReadOnlyCollection<string> documented = _contract.PropertiesOf("Message");
        IReadOnlyCollection<string> serialized = SerializedPropertyNames<MessageResponse>();

        List<string> missing = [.. documented.Where(d => !serialized.Contains(d, StringComparer.Ordinal))];

        Assert.True(
            missing.Count == 0,
            $"Message does not serialize {string.Join(", ", missing)}. Serialized: {string.Join(", ", serialized.Order())}");
    }

    /// <summary>
    /// <c>seq</c> is <c>int64</c> in the contract and must be <c>long</c> in the DTO.
    /// </summary>
    /// <remarks>
    /// Its own test because the failure is silent and distant. An <c>int</c> <c>seq</c> serializes
    /// identically for every realistic conversation, so nothing catches it until one overflows —
    /// and the sequence is the ordering authority, so overflow reorders history rather than
    /// erroring.
    /// </remarks>
    [Fact]
    public void The_message_sequence_is_a_sixty_four_bit_integer()
    {
        Assert.Equal("integer", _contract.TypeOfProperty("Message", "seq"));

        Assert.Equal(
            typeof(long),
            typeof(MessageResponse).GetProperty(nameof(MessageResponse.Seq))!.PropertyType);

        Assert.Equal(
            typeof(long),
            typeof(ConversationResponse).GetProperty(nameof(ConversationResponse.LastSeq))!.PropertyType);
    }

    /// <summary>
    /// <c>body</c> is nullable, because a deleted message is a tombstone with no body.
    /// </summary>
    /// <remarks>
    /// A non-nullable <c>string</c> here would make the DTO unable to represent the state the
    /// domain guarantees is reachable — <c>Message.Delete</c> sets <c>Body</c> to null — so the
    /// first delete in production would throw inside serialization rather than at a boundary check.
    /// </remarks>
    [Fact]
    public void A_deleted_message_can_be_represented_because_the_body_is_nullable()
    {
        Assert.True(IsNullableReference(typeof(MessageResponse), nameof(MessageResponse.Body)));

        MessageResponse tombstone = new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Seq: 7,
            Guid.CreateVersion7(),
            "01J000000000000000000000A",
            Body: null,
            DateTimeOffset.UtcNow,
            EditedAt: null,
            DeletedAt: DateTimeOffset.UtcNow,
            Mentions: [],
            Attachments: []);

        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(tombstone, SerializerOptions));

        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("body").ValueKind);
        Assert.Equal(JsonValueKind.String, document.RootElement.GetProperty("deletedAt").ValueKind);
    }

    /// <summary>
    /// The enum spellings the contract declares are the ones the API emits.
    /// </summary>
    /// <remarks>
    /// <c>from_join</c> in particular: a client comparing against <c>FromJoin</c> — the C# member
    /// name a default enum serializer would produce — falls through to its "show the whole history"
    /// branch and tells a new member they can read everything, which US3 scenario 4 says they
    /// cannot.
    /// </remarks>
    [Fact]
    public void The_documented_enum_spellings_are_what_the_api_uses()
    {
        Assert.Equal([ConversationKinds.Direct, ConversationKinds.Group], EnumValues("Conversation", "kind"));

        Assert.Equal(
            [HistoryVisibilities.FromJoin, HistoryVisibilities.Full],
            EnumValues("Conversation", "historyVisibility"));
    }

    /// <summary>
    /// The send request requires a client message key, and the DTO makes it non-optional.
    /// </summary>
    /// <remarks>
    /// FR-011 rests entirely on this value existing. A DTO that let it be omitted would accept the
    /// request, and the only place left to notice would be the dedup insert — by which point the
    /// caller has already been told the send succeeded.
    /// </remarks>
    [Fact]
    public void A_send_request_requires_a_client_message_key()
    {
        Assert.Contains("clientMessageKey", _contract.RequiredPropertiesOf("SendMessageRequest"));
        Assert.Contains("body", _contract.RequiredPropertiesOf("SendMessageRequest"));

        IReadOnlyCollection<string> serialized = SerializedPropertyNames<SendMessageRequest>();

        Assert.Contains("clientMessageKey", serialized);
        Assert.Contains("body", serialized);

        Assert.False(IsNullableReference(typeof(SendMessageRequest), nameof(SendMessageRequest.ClientMessageKey)));
    }

    /// <summary>
    /// A history page carries a keyset cursor and an explicit <c>hasMore</c>.
    /// </summary>
    /// <remarks>
    /// <c>hasMore</c> is asserted because the obvious client-side substitute — "a full page means
    /// there is probably another" — is wrong exactly at the boundary, where the last page is full
    /// and the client shows a spinner forever waiting for messages that do not exist.
    /// </remarks>
    [Fact]
    public void A_message_page_reports_its_cursor_and_whether_more_exists()
    {
        IReadOnlyCollection<string> documented = _contract.PropertiesOf("MessagePage");
        IReadOnlyCollection<string> serialized = SerializedPropertyNames<MessagePageResponse>();

        Assert.Contains("nextCursor", documented);
        Assert.Contains("hasMore", documented);

        foreach (string property in documented)
        {
            Assert.Contains(property, serialized);
        }

        Assert.Equal(
            typeof(bool),
            typeof(MessagePageResponse).GetProperty(nameof(MessagePageResponse.HasMore))!.PropertyType);
    }

    private IReadOnlyCollection<string> EnumValues(string schema, string property) =>
        _contract.EnumOfProperty(schema, property);

    private void AssertSatisfies<TResponse>(string schemaName)
    {
        IReadOnlyCollection<string> required = _contract.RequiredPropertiesOf(schemaName);

        Assert.NotEmpty(required);

        IReadOnlyCollection<string> serialized = SerializedPropertyNames<TResponse>();

        List<string> missing = [.. required.Where(r => !serialized.Contains(r, StringComparer.Ordinal))];

        Assert.True(
            missing.Count == 0,
            $"""
            {typeof(TResponse).Name} does not serialize {string.Join(", ", missing)}, which
            openapi.yaml declares required on '{schemaName}'.

            Serialized as: {string.Join(", ", serialized.Order())}
            """);
    }

    /// <summary>
    /// Whether a reference-typed property is declared nullable, read from the compiler's
    /// nullable metadata rather than guessed.
    /// </summary>
    private static bool IsNullableReference(Type type, string propertyName)
    {
        PropertyInfo property = type.GetProperty(propertyName)!;

        return new NullabilityInfoContext().Create(property).WriteState == NullabilityState.Nullable;
    }

    private static IReadOnlyCollection<string> SerializedPropertyNames<TResponse>()
    {
        object instance = CreateSample(typeof(TResponse));

        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(instance, SerializerOptions));

        return [.. document.RootElement.EnumerateObject().Select(p => p.Name)];
    }

    private static object CreateSample(Type type)
    {
        ConstructorInfo constructor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();

        object?[] arguments = [.. constructor.GetParameters().Select(p => Placeholder(p.ParameterType))];

        return constructor.Invoke(arguments);
    }

    private static object? Placeholder(Type type)
    {
        Type underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying == typeof(string))
        {
            return "sample";
        }

        return underlying.IsValueType ? Activator.CreateInstance(underlying) : null;
    }

    private IEnumerable<Endpoint> Endpoints() =>
        _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

}
