using System.Reflection;
using System.Text.RegularExpressions;
using InternalChat.Api.Hubs;
using InternalChat.TestSupport;
using Microsoft.AspNetCore.Authorization;

namespace InternalChat.ContractTests;

/// <summary>
/// T081 — <see cref="ChatHub"/> against <c>contracts/signalr-hub.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// SignalR has no schema document, so the contract is Markdown and this parses the two tables in
/// it. That is deliberate rather than lazy: the alternative is a list of method names duplicated in
/// C#, which is the same "compare the code to a copy of itself" trap
/// <see cref="OpenApiContract"/> exists to avoid. Parsing the table means renaming a hub method
/// without touching the document fails here.
/// </para>
/// <para>
/// A SignalR method is bound <b>by name, at runtime</b>. There is no compiler error, no 404, and no
/// startup failure when a client invokes a method the server does not have — the invocation simply
/// never completes. The same is true of server-to-client events, where a renamed event is silently
/// delivered to nobody. Both directions are therefore asserted here, because neither has any other
/// safety net.
/// </para>
/// </remarks>
public sealed class HubContractTests
{
    private readonly HubContract _contract = HubContract.Load();

    /// <summary>
    /// Every client-callable method the contract documents exists on the hub, spelled identically.
    /// </summary>
    [Fact]
    public void Every_documented_client_to_server_method_exists_on_the_hub()
    {
        IReadOnlyCollection<string> documented = _contract.ClientToServerMethods;

        Assert.NotEmpty(documented);

        IReadOnlyCollection<string> implemented = HubMethods();

        List<string> missing = [.. documented.Where(d => !implemented.Contains(d, StringComparer.Ordinal))];

        Assert.True(
            missing.Count == 0,
            $"""
            signalr-hub.md documents {string.Join(", ", missing)} as client-callable, and ChatHub
            does not declare them.

            A client invoking a hub method the server lacks gets no error — the call just never
            completes — so nothing else in the system would report this.

            ChatHub declares: {string.Join(", ", implemented.Order())}
            """);
    }

    /// <summary>
    /// The hub declares no public method the contract does not document.
    /// </summary>
    /// <remarks>
    /// The other direction, and the one that matters for Principle IV. Every hub method is a
    /// remotely invocable entry point; an undocumented one is a surface nobody reviewed and that
    /// <c>AuthorizationCoverageTests</c> is the only thing standing in front of.
    /// </remarks>
    [Fact]
    public void The_hub_declares_no_method_the_contract_does_not_document()
    {
        IReadOnlyCollection<string> documented = _contract.ClientToServerMethods;

        List<string> undocumented = [.. HubMethods().Where(m => !documented.Contains(m, StringComparer.Ordinal))];

        Assert.True(
            undocumented.Count == 0,
            $"""
            ChatHub declares {string.Join(", ", undocumented)}, which signalr-hub.md does not
            document. Every hub method is a remotely invocable entry point; add it to the contract
            or make it private.
            """);
    }

    /// <summary>
    /// The hub requires authentication.
    /// </summary>
    /// <remarks>
    /// Asserted here as well as in the architecture suite because the consequence is specific:
    /// without it every server-to-client event below is deliverable to an anonymous connection.
    /// </remarks>
    [Fact]
    public void The_hub_requires_authentication()
    {
        Assert.NotNull(typeof(ChatHub).GetCustomAttribute<AuthorizeAttribute>());
    }

    /// <summary>
    /// The contract documents the events US2 delivers, and they are spelled as the client expects.
    /// </summary>
    /// <remarks>
    /// Constants rather than string literals at the call sites, so the fan-out consumer and this
    /// test cannot disagree — a renamed event that compiles is otherwise delivered to nobody.
    /// </remarks>
    [Theory]
    [InlineData(nameof(ChatHubEvents.MessageReceived))]
    [InlineData(nameof(ChatHubEvents.MessageEdited))]
    [InlineData(nameof(ChatHubEvents.MessageDeleted))]
    [InlineData(nameof(ChatHubEvents.ConversationCreated))]
    [InlineData(nameof(ChatHubEvents.TypingChanged))]
    [InlineData(nameof(ChatHubEvents.PresenceChanged))]

    // 002 — hub contract 1.1.0, additive: the negotiated transport, so a client on long polling can
    // say so (FR-010).
    [InlineData(nameof(ChatHubEvents.ConnectionInfo))]
    public void Every_delivered_event_is_documented(string eventName)
    {
        Assert.Contains(eventName, _contract.ServerToClientEvents);

        string? constant = typeof(ChatHubEvents)
            .GetField(eventName, BindingFlags.Public | BindingFlags.Static)
            ?.GetValue(null) as string;

        Assert.Equal(eventName, constant);
    }

    /// <summary>
    /// Messages are sent over HTTP, never over the hub.
    /// </summary>
    /// <remarks>
    /// The contract is explicit about this and the reason is worth protecting: the idempotent send
    /// path (FR-011) lives on one transport with clear status codes. A <c>SendMessage</c> hub
    /// method would duplicate retry semantics across two transports, and the hub half has no status
    /// codes to express "already accepted, here is the original".
    /// </remarks>
    [Fact]
    public void The_hub_offers_no_way_to_send_a_message()
    {
        Assert.DoesNotContain("SendMessage", HubMethods());
        Assert.DoesNotContain("SendMessage", _contract.ClientToServerMethods);
    }

    /// <summary>
    /// Public instance methods declared on the hub itself.
    /// </summary>
    /// <remarks>
    /// <c>DeclaredOnly</c> so the base <see cref="Microsoft.AspNetCore.SignalR.Hub"/>'s members —
    /// <c>Dispose</c>, <c>OnConnectedAsync</c>, and the rest — are not reported as undocumented
    /// contract violations. Property accessors are filtered for the same reason.
    /// </remarks>
    private static IReadOnlyCollection<string> HubMethods() =>
    [
        .. typeof(ChatHub)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)

            // DeclaredOnly is not sufficient on its own: an OVERRIDE is declared on the derived
            // type, so `OnConnectedAsync` came through and was reported as an undocumented
            // client-callable method. It is not one — SignalR invokes the lifetime overrides
            // itself and does not expose them to clients. Comparing against the base definition
            // is what separates "new method on ChatHub" from "override of a Hub member".
            .Where(m => m.GetBaseDefinition().DeclaringType == m.DeclaringType)
            .Select(m => m.Name),
    ];
}

/// <summary>
/// Parses the method and event tables out of <c>contracts/signalr-hub.md</c>.
/// </summary>
internal sealed partial class HubContract
{
    private readonly string _markdown;

    private HubContract(string markdown) => _markdown = markdown;

    /// <summary>Loads the committed hub contract.</summary>
    public static HubContract Load()
    {
        string path = RepositoryPaths.SignalRContract;

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"The hub contract was not found at '{path}'. Without it these tests would assert "
                + "nothing while appearing to pass.",
                path);
        }

        return new HubContract(File.ReadAllText(path));
    }

    /// <summary>Method names from the "Client → Server methods" table.</summary>
    public IReadOnlyCollection<string> ClientToServerMethods => NamesInSectionAfter("## Client → Server methods");

    /// <summary>Event names from the "Server → Client events" table.</summary>
    public IReadOnlyCollection<string> ServerToClientEvents => NamesInSectionAfter("## Server → Client events");

    /// <summary>
    /// The first backticked cell of every table row in the section starting at
    /// <paramref name="heading"/>.
    /// </summary>
    /// <remarks>
    /// Reads to the next <c>##</c> heading rather than to the end of the document, so the two
    /// tables cannot bleed into each other — which would make the "no undocumented methods" test
    /// pass by accidentally accepting every event name as a method name too.
    /// </remarks>
    private IReadOnlyCollection<string> NamesInSectionAfter(string heading)
    {
        int start = _markdown.IndexOf(heading, StringComparison.Ordinal);

        if (start < 0)
        {
            throw new InvalidOperationException(
                $"signalr-hub.md has no '{heading}' section. Either it was renamed without this "
                + "test following, or the contract lost the table these assertions read.");
        }

        start += heading.Length;

        int end = _markdown.IndexOf("\n## ", start, StringComparison.Ordinal);
        string section = end < 0 ? _markdown[start..] : _markdown[start..end];

        return
        [
            .. RowName()
                .Matches(section)
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.Ordinal),
        ];
    }

    /// <summary>Matches the leading <c>| `Name` |</c> cell of a table row.</summary>
    [GeneratedRegex(@"^\|\s*`(\w+)`\s*\|", RegexOptions.Multiline)]
    private static partial Regex RowName();
}
