using System.Reflection;
using System.Text.Json;
using InternalChat.Api.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace InternalChat.ContractTests;

/// <summary>
/// T160 — the search endpoint against <c>contracts/openapi.yaml</c>.
/// </summary>
/// <remarks>
/// <para>
/// One assertion here is deliberately a <b>negative</b> one, and it is the important one:
/// <c>SearchResultPageResponse</c> must carry <c>truncated</c>. FR-033 says the system either
/// returns results within the stated time or says explicitly that they were truncated, and a
/// response shape with nowhere to say so makes the second half unimplementable — silently, and in a
/// way no functional test would catch, because a truncated search and an exhaustive one look
/// identical from the outside.
/// </para>
/// </remarks>
public sealed class SearchContractTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly WebApplicationFactory<Program> _factory;
    private readonly OpenApiContract _contract = OpenApiContract.Load();

    public SearchContractTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public void The_documented_search_operation_is_mapped()
    {
        const string path = "/search/messages";

        Assert.Contains(path, _contract.Paths);
        Assert.Contains("GET", _contract.MethodsFor(path));

        string expected = _contract.BasePath + path;

        bool mapped = Endpoints().Any(endpoint =>
            string.Equals(RoutePatterns.Of(endpoint), expected, StringComparison.OrdinalIgnoreCase)
            && RoutePatterns.MethodsOf(endpoint).Contains("GET", StringComparer.OrdinalIgnoreCase));

        Assert.True(
            mapped,
            $"""
            The contract documents GET {expected}, and no endpoint serves it.

            Mapped routes:
              {string.Join("\n  ", Endpoints().Select(e => $"{string.Join('|', RoutePatterns.MethodsOf(e))} {RoutePatterns.Of(e)}").Order())}
            """);
    }

    /// <summary>
    /// The page can say it was cut short.
    /// </summary>
    /// <remarks>
    /// Its own test because the failure is silent by construction: a client cannot tell a truncated
    /// result set from a complete one, so nothing except this assertion would notice the field
    /// going missing — and the consequence is a user concluding content does not exist.
    /// </remarks>
    [Fact]
    public void A_search_page_can_state_that_results_were_truncated()
    {
        Assert.Contains("truncated", _contract.PropertiesOf("SearchResultPage"));

        IReadOnlyCollection<string> serialized = SerializedPropertyNames<SearchResultPageResponse>();

        Assert.Contains("truncated", serialized);
        Assert.Contains("nextCursor", serialized);

        Assert.Equal(
            typeof(bool),
            typeof(SearchResultPageResponse)
                .GetProperty(nameof(SearchResultPageResponse.Truncated))!.PropertyType);
    }

    /// <summary>
    /// A result carries what jump-to-context needs (FR-031).
    /// </summary>
    /// <remarks>
    /// <c>seq</c> rather than only an id: the history endpoint pages by sequence, so a result
    /// without one could be identified but not navigated to — which is FR-031 unimplementable for
    /// the same reason as above.
    /// </remarks>
    [Fact]
    public void A_search_result_can_be_navigated_to()
    {
        IReadOnlyCollection<string> serialized = SerializedPropertyNames<SearchResultResponse>();

        Assert.Contains("conversationId", serialized);
        Assert.Contains("messageId", serialized);
        Assert.Contains("seq", serialized);
        Assert.Contains("highlight", serialized);

        Assert.Equal(
            typeof(long),
            typeof(SearchResultResponse).GetProperty(nameof(SearchResultResponse.Seq))!.PropertyType);
    }

    [Fact]
    public void A_direct_conversation_result_can_omit_a_name()
    {
        // A direct conversation has no name — the client renders the other participant — so a
        // non-nullable field here would force the API to invent one.
        Assert.True(IsNullableReference(
            typeof(SearchResultResponse), nameof(SearchResultResponse.ConversationName)));

        SearchResultResponse direct = new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            ConversationName: null,
            Seq: 42,
            "a <mark>runbook</mark> for the release",
            Rank: 0.73f);

        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(direct, SerializerOptions));

        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("conversationName").ValueKind);
    }

    /// <summary>
    /// Every documented filter is a parameter the endpoint accepts.
    /// </summary>
    /// <remarks>
    /// FR-030 requires filtering by person, conversation, date range, and attachment type. A
    /// documented filter the endpoint does not bind is worse than one that does not exist: the
    /// request succeeds, the filter is ignored, and the caller gets more results than they asked
    /// for without any indication.
    /// </remarks>
    [Fact]
    public void Every_documented_filter_is_bound_by_the_endpoint()
    {
        IReadOnlyCollection<string> documented = _contract.QueryParametersFor("/search/messages", "GET");

        foreach (string expected in new[]
            { "q", "conversationId", "authorId", "from", "to", "hasAttachment", "cursor", "limit" })
        {
            Assert.Contains(expected, documented);
        }

        // The Application request exposes one property per documented filter. Checked on the
        // request record rather than on the endpoint's signature because minimal-API parameter
        // binding is not reflectable in a way worth asserting against.
        IReadOnlyCollection<string> bound =
            [.. typeof(Application.Search.SearchMessages).GetProperties().Select(p => p.Name)];

        Assert.Contains("ConversationId", bound);
        Assert.Contains("AuthorId", bound);
        Assert.Contains("From", bound);
        Assert.Contains("To", bound);
        Assert.Contains("HasAttachment", bound);
        Assert.Contains("Cursor", bound);
        Assert.Contains("Limit", bound);
    }

    private IEnumerable<Endpoint> Endpoints() =>
        _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

    private static IReadOnlyCollection<string> SerializedPropertyNames<T>()
    {
        object instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(T));
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize((T)instance, SerializerOptions));

        return [.. document.RootElement.EnumerateObject().Select(p => p.Name)];
    }

    private static bool IsNullableReference(Type type, string propertyName)
    {
        PropertyInfo property = type.GetProperty(propertyName)!;
        NullabilityInfoContext context = new();

        return context.Create(property).WriteState is NullabilityState.Nullable;
    }
}
