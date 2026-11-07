using System.Text;
using System.Text.Json;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Application.Tests.Commands;

namespace CharacterPlanner.Application.Tests.Sessions;

/// <summary>The live-session endpoint without a transport: strict requests, revision guards and change waits.</summary>
public sealed class SessionEndpointTests
{
    private static PlannerSession NewSession() => new(Division.Pack, Division.State(), Division.Applier);

    private static async Task<JsonElement> SendAsync(SessionEndpoint endpoint, string request) =>
        JsonDocument.Parse(await endpoint.HandleAsync(Encoding.UTF8.GetBytes(request), TestContext.Current.CancellationToken)).RootElement;

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"command":"reset"}""")]
    [InlineData("""{"command":"inspect","expectedRevision":0}""")]
    [InlineData("""{"command":"undo"}""")]
    [InlineData("""{"command":"undo","expectedRevision":-1}""")]
    [InlineData("""{"command":"undo","expectedRevision":1.5}""")]
    [InlineData("""{"command":"apply","expectedRevision":0}""")]
    [InlineData("""{"command":"inspect","waitMilliseconds":60001}""")]
    [InlineData("""{"command":"inspect","sinceRevision":0}""")]
    [InlineData("""{"command":"inspect","command":"inspect"}""")]
    [InlineData("""{"command":"\uD800"}""")]
    [InlineData("not json")]
    public async Task MalformedRequestsAreRefusedWithoutChangingTheSession(string request)
    {
        var session = NewSession();
        var reply = await SendAsync(new SessionEndpoint(session), request);
        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal(SessionRequests.RequestInvalid, reply.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, reply.GetProperty("snapshot").GetProperty("revision").GetInt64());
        Assert.Equal(0, session.Snapshot.Revision);
    }

    [Fact]
    public async Task AppliedEditsAreSessionHistoryAndAStaleRevisionChangesNothing()
    {
        var session = NewSession();
        var endpoint = new SessionEndpoint(session);
        var applied = await SendAsync(endpoint, """{"command":"apply","expectedRevision":0,"operations":[{"kind":"name","text":"Live"}]}""");
        Assert.True(applied.GetProperty("ok").GetBoolean());
        Assert.Equal(1, applied.GetProperty("snapshot").GetProperty("revision").GetInt64());
        Assert.Equal("session", applied.GetProperty("snapshot").GetProperty("history")[1].GetProperty("origin").GetString());
        Assert.Equal(EditOrigin.Session, session.Snapshot.History[^1].Origin);

        var stale = await SendAsync(endpoint, """{"command":"apply","expectedRevision":0,"operations":[{"kind":"name","text":"Late"}]}""");
        Assert.Equal(SessionErrorCodes.StaleRevision, stale.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Live", session.Snapshot.State.Build.Meta.Name);
        var unreadable = await SendAsync(endpoint, """{"command":"apply","expectedRevision":1,"operations":{"kind":"name"}}""");
        Assert.Equal(SessionRequests.OperationsInvalid, unreadable.GetProperty("error").GetProperty("code").GetString());
        Assert.NotEmpty(unreadable.GetProperty("operationErrors").EnumerateArray());
    }

    [Fact]
    public async Task AWaitReturnsTheFirstNewerRevisionOrTheCurrentOneWhenItEnds()
    {
        var session = NewSession();
        var endpoint = new SessionEndpoint(session);
        var quiet = await SendAsync(endpoint, """{"command":"inspect","waitMilliseconds":50}""");
        Assert.Equal(0, quiet.GetProperty("snapshot").GetProperty("revision").GetInt64());
        var waiting = SendAsync(endpoint, """{"command":"inspect","sinceRevision":0,"waitMilliseconds":10000}""");
        await Task.Delay(100, TestContext.Current.CancellationToken);
        session.Apply([new NameOperation("Changed")], 0, EditOrigin.Desktop);
        Assert.Equal(1, (await waiting).GetProperty("snapshot").GetProperty("revision").GetInt64());
        // An older since-revision answers at once.
        var behind = await SendAsync(endpoint, """{"command":"inspect","sinceRevision":0,"waitMilliseconds":10000}""");
        Assert.Equal(1, behind.GetProperty("snapshot").GetProperty("revision").GetInt64());
    }
}
