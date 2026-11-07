using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Sessions;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Infrastructure.PackLoading;
using CharacterPlanner.Infrastructure.Sessions;

namespace CharacterPlanner.Cli.Tests;

public sealed partial class CliAppTests
{
    private string Sessions => Path.Combine(root, "sessions");

    /// <summary>A live session on the small context pack, served on a real pipe and registered in a temporary registry.</summary>
    private async Task<(SessionPipeHost Host, PlannerSession Session)> StartSessionAsync()
    {
        var directory = WriteMini(pack: WithContext);
        var loaded = await PackFileLoader.LoadAsync(directory, TestContext.Current.CancellationToken);
        var compiled = loaded.Validation.Compiled!;
        var session = new PlannerSession(compiled, PlannerState.Of(BuildSnapshot.Create(compiled.Pack).WithEquipment("mask", "mask_a")), new OperationApplier(compiled));
        var host = await SessionPipeHost.StartAsync(session, directory, loaded.ManifestSha256, Sessions, TestContext.Current.CancellationToken);
        return (host, session);
    }

    private static Task<Run> RunWithInputAsync(string input, params string[] args) => RunWithBytesAsync(Encoding.UTF8.GetBytes(input), args);

    private static async Task<Run> RunWithBytesAsync(byte[] input, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var stdin = new MemoryStream(input);
        var exitCode = await CliApp.RunAsync(args, output, error, stdin);
        return new(exitCode, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task SessionListInspectAndHistoryDescribeTheWindowWithoutItsEndpoint()
    {
        var (host, _) = await StartSessionAsync();
        await using (host)
        {
            var listed = await RunAsync("session", "list", "--session-dir", Sessions);
            Assert.Equal(CliApp.Success, listed.ExitCode);
            var entry = Assert.Single(listed.Json.GetProperty("result").GetProperty("sessions").EnumerateArray());
            Assert.Equal(host.Registration.SessionId, entry.GetProperty("sessionId").GetString());
            Assert.True(entry.GetProperty("available").GetBoolean());
            Assert.Equal(0, entry.GetProperty("revision").GetInt64());
            Assert.Equal("mini", entry.GetProperty("packId").GetString());
            Assert.DoesNotContain(host.Registration.Token, listed.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(host.Registration.Pipe, listed.Output, StringComparison.Ordinal);

            var inspected = await RunAsync("session", "inspect", "--session-dir", Sessions);
            Assert.Equal(CliApp.Success, inspected.ExitCode);
            var result = inspected.Json.GetProperty("result");
            Assert.Equal(0, result.GetProperty("revision").GetInt64());
            Assert.Equal("mask_a", BuildDocument.Decode(System.Text.Encoding.UTF8.GetBytes(result.GetProperty("document").GetRawText())).Equipment["mask"]);
            Assert.False(result.GetProperty("canUndo").GetBoolean());
            var history = await RunAsync("session", "history", "--session", host.Registration.SessionId, "--session-dir", Sessions);
            Assert.Equal("initial", Assert.Single(history.Json.GetProperty("result").GetProperty("history").EnumerateArray()).GetProperty("origin").GetString());
        }
        // Closing removes the registration.
        Assert.Empty((await RunAsync("session", "list", "--session-dir", Sessions)).Json.GetProperty("result").GetProperty("sessions").EnumerateArray());
    }

    [Fact]
    public async Task SessionEditsNameTheirRevisionAndAStaleOneIsRefusedWithTheCurrentSnapshot()
    {
        var (host, session) = await StartSessionAsync();
        await using var _ = host;
        var operations = WriteOperations("""[{"kind":"context","id":"bonus","value":2},{"kind":"name","text":"Live"}]""");
        var applied = await RunAsync("session", "apply", "--expected-revision", "0", "--operations", operations, "--session-dir", Sessions);
        Assert.Equal(CliApp.Success, applied.ExitCode);
        Assert.Equal(1, applied.Json.GetProperty("result").GetProperty("revision").GetInt64());
        Assert.Equal(2, applied.Json.GetProperty("result").GetProperty("context").GetProperty("bonus").GetDouble());
        Assert.Equal(2, applied.Json.GetProperty("result").GetProperty("changes").GetArrayLength());
        Assert.Equal(1, session.Snapshot.Revision);
        Assert.Equal(EditOrigin.Session, session.Snapshot.History[^1].Origin);
        Assert.Equal("Live", session.Snapshot.State.Build.Meta.Name);

        var stale = await RunAsync("session", "apply", "--expected-revision", "0", "--operations", operations, "--session-dir", Sessions);
        Assert.Equal(CliApp.StaleRevision, stale.ExitCode);
        Assert.Contains(Diagnostics(stale, false), diagnostic => diagnostic.Code == SessionErrorCodes.StaleRevision);
        Assert.Equal(1, stale.Json.GetProperty("result").GetProperty("revision").GetInt64());
        Assert.Equal(1, session.Snapshot.Revision);

        var undone = await RunAsync("session", "undo", "--expected-revision", "1", "--session-dir", Sessions);
        Assert.Equal(CliApp.Success, undone.ExitCode);
        Assert.Equal("New Build", session.Snapshot.State.Build.Meta.Name);
        var redone = await RunAsync("session", "redo", "--expected-revision", undone.Json.GetProperty("result").GetProperty("revision").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--session-dir", Sessions);
        Assert.Equal(CliApp.Success, redone.ExitCode);
        Assert.Equal("Live", session.Snapshot.State.Build.Meta.Name);
    }

    [Fact]
    public async Task SessionApplyReadsStandardInputAndReportsRejectedOperations()
    {
        var (host, session) = await StartSessionAsync();
        await using var _ = host;
        var applied = await RunWithInputAsync("""[{"kind":"name","text":"From stdin"}]""", "session", "apply", "--expected-revision", "0", "--operations", "-", "--session-dir", Sessions);
        Assert.Equal(CliApp.Success, applied.ExitCode);
        Assert.Equal("From stdin", session.Snapshot.State.Build.Meta.Name);

        var rejected = await RunWithInputAsync("""[{"kind":"equipment","slot":"mask","id":"no_such_mask"}]""", "session", "apply", "--expected-revision", "1", "--operations", "-", "--session-dir", Sessions);
        Assert.Equal(CliApp.InvalidInput, rejected.ExitCode);
        Assert.Contains(Diagnostics(rejected, false), diagnostic => diagnostic.Code == SessionErrorCodes.OperationsRejected);
        Assert.NotEmpty(rejected.Json.GetProperty("operationErrors").EnumerateArray());
        Assert.Equal(1, session.Snapshot.Revision);
        var unreadable = await RunWithInputAsync("""[{"kind":"nope"}]""", "session", "apply", "--expected-revision", "1", "--operations", "-", "--session-dir", Sessions);
        Assert.Contains(Diagnostics(unreadable, false), diagnostic => diagnostic.Code == SessionRequests.OperationsInvalid);
    }

    [Fact]
    public async Task SessionResolveMatchesBuildResolveOnTheWindowsBuild()
    {
        var (host, session) = await StartSessionAsync();
        await using var _ = host;
        session.Apply([new ContextOperation("bonus", 3)], 0, EditOrigin.Desktop);
        var live = await RunAsync("session", "resolve", "--session-dir", Sessions);
        Assert.Equal(CliApp.Success, live.ExitCode);
        var result = live.Json.GetProperty("result");
        Assert.Equal(1, result.GetProperty("revision").GetInt64());
        // The mask adds 10 to the bonus assumption of 3.
        Assert.Equal(13, result.GetProperty("stats").GetProperty("armor").GetDouble());

        var path = Path.Combine(root, "live.json");
        File.WriteAllBytes(path, BuildDocument.Encode(session.Snapshot.State.Build));
        var context = Path.Combine(root, "live-context.json");
        File.WriteAllText(context, """{"bonus":3}""");
        var offline = await RunAsync("build", "resolve", Path.Combine(root, "mini"), path, "--context", context);
        Assert.Equal(offline.Json.GetProperty("result").GetProperty("stats").GetRawText(), result.GetProperty("stats").GetRawText());
    }

    [Fact]
    public async Task InspectCanWaitForTheNextRevision()
    {
        var (host, session) = await StartSessionAsync();
        await using var _ = host;
        var quiet = await RunAsync("session", "inspect", "--wait", "150", "--session-dir", Sessions);
        Assert.Equal(0, quiet.Json.GetProperty("result").GetProperty("revision").GetInt64());
        var waiting = RunAsync("session", "inspect", "--wait", "10000", "--since-revision", "0", "--session-dir", Sessions);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        session.Apply([new NameOperation("Changed")], 0, EditOrigin.Desktop);
        var woke = await waiting;
        Assert.Equal(1, woke.Json.GetProperty("result").GetProperty("revision").GetInt64());
        Assert.Equal(CliApp.UsageError, (await RunAsync("session", "inspect", "--since-revision")).ExitCode);
        var sinceOnly = await RunAsync("session", "inspect", "--since-revision", "0", "--session-dir", Sessions);
        Assert.Contains(Diagnostics(sinceOnly, false), diagnostic => diagnostic.Code == "session-request-invalid");
    }

    [Fact]
    public async Task SessionSelectionNeedsExactlyOneAvailableWindow()
    {
        var none = await RunAsync("session", "inspect", "--session-dir", Sessions);
        Assert.Equal(CliApp.IoFailure, none.ExitCode);
        Assert.Contains(Diagnostics(none, false), diagnostic => diagnostic.Code == "session-none");

        var (first, _) = await StartSessionAsync();
        await using var _1 = first;
        // A crashed window's registration stays behind, listed as unavailable and never selected.
        await SessionRegistry.PublishAsync(Sessions, first.Registration with { SessionId = new string('c', 32), Pipe = "CharacterPlanner-gone-" + Guid.NewGuid().ToString("N") },
            TestContext.Current.CancellationToken);
        var listed = (await RunAsync("session", "list", "--session-dir", Sessions)).Json.GetProperty("result").GetProperty("sessions").EnumerateArray().ToArray();
        Assert.Equal(2, listed.Length);
        Assert.False(listed.Single(entry => entry.GetProperty("sessionId").GetString() == new string('c', 32)).GetProperty("available").GetBoolean());
        Assert.Equal(CliApp.Success, (await RunAsync("session", "inspect", "--session-dir", Sessions)).ExitCode);

        var (second, _) = await StartSessionAsync();
        await using var _2 = second;
        var ambiguous = await RunAsync("session", "inspect", "--session-dir", Sessions);
        Assert.Equal(CliApp.IoFailure, ambiguous.ExitCode);
        Assert.Contains(Diagnostics(ambiguous, false), diagnostic => diagnostic.Code == "session-ambiguous");
        Assert.Equal(CliApp.Success, (await RunAsync("session", "inspect", "--session", second.Registration.SessionId, "--session-dir", Sessions)).ExitCode);
        var unknown = await RunAsync("session", "inspect", "--session", new string('d', 32), "--session-dir", Sessions);
        Assert.Contains(Diagnostics(unknown, false), diagnostic => diagnostic.Code == "session-not-found");
    }

    [Fact]
    public async Task TheWindowRefusesARequestWithoutItsToken()
    {
        var (host, session) = await StartSessionAsync();
        await using var _ = host;
        using var reply = await SessionPipe.SendAsync(host.Registration with { Token = SessionPipe.NewToken() },
            new JsonObject { ["command"] = "apply", ["expectedRevision"] = 0, ["operations"] = new JsonArray(new JsonObject { ["kind"] = "name", ["text"] = "Intruder" }) },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(SessionPipe.Unauthorized, reply.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, session.Snapshot.Revision);
    }

    [Theory]
    [InlineData("session", "apply", "--expected-revision", "0")]
    [InlineData("session", "apply", "--operations", "ops.json")]
    [InlineData("session", "undo")]
    [InlineData("session", "list", "--session", "x")]
    [InlineData("session", "resolve", "--wait", "5")]
    public async Task MalformedSessionArgumentsAreUsageErrors(params string[] args)
    {
        var run = await RunAsync(args);
        Assert.Equal(CliApp.UsageError, run.ExitCode);
        AssertInvalidCommand(run);
    }

    [Fact]
    public async Task ARevisionMustBeANonNegativeNumber()
    {
        var run = await RunAsync("session", "undo", "--expected-revision", "-1", "--session-dir", Sessions);
        Assert.Equal(CliApp.InvalidInput, run.ExitCode);
        Assert.Contains(Diagnostics(run, false), diagnostic => diagnostic.Code == "session-request-invalid");
        Assert.True(JsonDocument.Parse(run.Output).RootElement.TryGetProperty("diagnostics", out _));
    }

    [Fact]
    public async Task StandardInputIsUtf8BytesBoundedLikeAFile()
    {
        var (host, session) = await StartSessionAsync();
        await using var _ = host;
        var applied = await RunWithInputAsync("""[{"kind":"name","text":"Résumé ✓"}]""", "session", "apply", "--expected-revision", "0", "--operations", "-", "--session-dir", Sessions);
        Assert.Equal(CliApp.Success, applied.ExitCode);
        Assert.Equal("Résumé ✓", session.Snapshot.State.Build.Meta.Name);
        // Fewer characters than the limit, but more bytes.
        var large = Encoding.UTF8.GetBytes("[\"" + new string('é', BuildDocument.MaxBytes / 2 + 16) + "\"]");
        var refused = await RunWithBytesAsync(large, "session", "apply", "--expected-revision", "1", "--operations", "-", "--session-dir", Sessions);
        Assert.Contains(Diagnostics(refused, false), diagnostic => diagnostic.Code == "input-too-large");
    }

    [Fact]
    public async Task DeadWindowsAreSkippedQuicklyPrunedAndNeverHideALiveOne()
    {
        // Registrations of ended processes, and plenty of junk names that sort first, ahead of a live window.
        Directory.CreateDirectory(Sessions);
        for (var i = 0; i < SessionRegistry.Limit + 5; i++) { File.WriteAllText(Path.Combine(Sessions, $"0junk{i:D4}.json"), "{}"); }
        var dead = new SessionRegistration(new string('a', 32), int.MaxValue - 7, DateTimeOffset.UtcNow, "mini", "1", root, "x", "CharacterPlanner-dead", SessionPipe.NewToken());
        await SessionRegistry.PublishAsync(Sessions, dead, TestContext.Current.CancellationToken);
        // A live process id that registered "before" it started: a reused id, so also dead.
        var reused = dead with { SessionId = new string('b', 32), ProcessId = Environment.ProcessId, StartedAt = DateTimeOffset.UtcNow.AddYears(-1) };
        await SessionRegistry.PublishAsync(Sessions, reused, TestContext.Current.CancellationToken);
        var listedDead = (await RunAsync("session", "list", "--session-dir", Sessions)).Json.GetProperty("result").GetProperty("sessions").EnumerateArray().ToArray();
        Assert.All(listedDead, entry => Assert.Contains("no longer running", entry.GetProperty("problem").GetString(), StringComparison.Ordinal));

        // Starting a window prunes them.
        var (host, _) = await StartSessionAsync();
        await using var _1 = host;
        var listed = (await RunAsync("session", "list", "--session-dir", Sessions)).Json.GetProperty("result").GetProperty("sessions").EnumerateArray().ToArray();
        Assert.Equal(host.Registration.SessionId, Assert.Single(listed).GetProperty("sessionId").GetString());

        // Unresponsive registrations of a running process are probed in parallel, not one after another.
        for (var i = 0; i < 4; i++)
        {
            await SessionRegistry.PublishAsync(Sessions, host.Registration with { SessionId = $"{i:D1}" + new string('e', 31), Pipe = "CharacterPlanner-gone-" + i },
                TestContext.Current.CancellationToken);
        }
        var timer = Stopwatch.StartNew();
        Assert.Equal(CliApp.Success, (await RunAsync("session", "inspect", "--session-dir", Sessions)).ExitCode);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(6), $"Selection took {timer.Elapsed}");
    }

    [Fact]
    public async Task WaitingClientsAndBadRequestsNeverStopTheWindowServing()
    {
        var (host, session) = await StartSessionAsync();
        await using var _ = host;
        // The clients name the window: this checks that waiting clients don't stop it serving, not discovery under load.
        var waits = Enumerable.Range(0, 8).Select(_ => RunAsync("session", "inspect", "--wait", "4000", "--since-revision", "0",
            "--session", host.Registration.SessionId, "--session-dir", Sessions)).ToArray();
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var timer = Stopwatch.StartNew();
        Assert.Equal(CliApp.Success, (await RunAsync("session", "inspect", "--session", host.Registration.SessionId, "--session-dir", Sessions)).ExitCode);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3), $"Inspect behind waiting clients took {timer.Elapsed}");

        using (var surrogate = await SessionPipe.SendAsync(host.Registration with { Token = "\uD800" }, new JsonObject { ["command"] = "inspect" },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))
        { Assert.Equal(SessionPipe.Unauthorized, surrogate.RootElement.GetProperty("error").GetProperty("code").GetString()); }
        session.Apply([new NameOperation("Done")], 0, EditOrigin.Desktop);
        Assert.All(await Task.WhenAll(waits), wait => Assert.Equal(1, wait.Json.GetProperty("result").GetProperty("revision").GetInt64()));
        Assert.Equal(CliApp.Success, (await RunAsync("session", "inspect", "--session-dir", Sessions)).ExitCode);
    }

    [Fact]
    public async Task ASilentButRunningWindowIsProbedOnceMoreBeforeNoneIsReported()
    {
        // A window busy for longer than the probe (here: the first connection is held unanswered) must not be reported as
        // missing. The fake window relays its second request to a real one.
        var (host, _) = await StartSessionAsync();
        await using var _1 = host;
        var busy = Path.Combine(root, "busy-sessions");
        var fake = host.Registration with { SessionId = new string('f', 32), Pipe = "CharacterPlanner-busy-" + Guid.NewGuid().ToString("N"), Token = SessionPipe.NewToken() };
        await SessionRegistry.PublishAsync(busy, fake, TestContext.Current.CancellationToken);
        var connections = 0;
        async Task<byte[]> Read(Stream stream)
        {
            var length = new byte[4];
            await stream.ReadExactlyAsync(length, TestContext.Current.CancellationToken);
            var message = new byte[BinaryPrimitives.ReadInt32LittleEndian(length)];
            await stream.ReadExactlyAsync(message, TestContext.Current.CancellationToken);
            return message;
        }
        NamedPipeServerStream Listen() => new(fake.Pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var silentConnected = new TaskCompletionSource();
        // The first connection is read and left unanswered past the probe timeout; only then does the relay start
        // listening, so the first probe always reaches the silent instance.
        var silent = Task.Run(async () =>
        {
            await using var pipe = Listen();
            await pipe.WaitForConnectionAsync(stop.Token);
            Interlocked.Increment(ref connections);
            silentConnected.SetResult();
            await Read(pipe);
            await Task.Delay(TimeSpan.FromSeconds(3), stop.Token);
        }, stop.Token);
        var relay = Task.Run(async () =>
        {
            await silentConnected.Task;
            while (!stop.IsCancellationRequested)
            {
                await using var pipe = Listen();
                await pipe.WaitForConnectionAsync(stop.Token);
                Interlocked.Increment(ref connections);
                var request = JsonNode.Parse(await Read(pipe))!["request"]!.AsObject();
                using var reply = await SessionPipe.SendAsync(host.Registration, request.DeepClone().AsObject(), TimeSpan.FromSeconds(10), stop.Token);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(reply.RootElement);
                var length = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
                await pipe.WriteAsync(length, stop.Token);
                await pipe.WriteAsync(bytes, stop.Token);
                await pipe.FlushAsync(stop.Token);
                // Like the real window, keep the pipe until the client has read the reply and closed it.
                _ = await pipe.ReadAsync(new byte[1], stop.Token);
            }
        }, stop.Token);
        var run = await RunAsync("session", "inspect", "--session-dir", busy);
        await stop.CancelAsync();
        // Silent probe, second probe, then the inspect request itself.
        Assert.Equal(CliApp.Success, run.ExitCode);
        Assert.Equal(3, Volatile.Read(ref connections));
        await Task.WhenAll(silent, relay).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    [Fact]
    public async Task AClientThatNeverReadsCannotHoldUpClosing()
    {
        var (host, _) = await StartSessionAsync();
        var client = new NamedPipeClientStream(".", host.Registration.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using (client)
        {
            await client.ConnectAsync(TestContext.Current.CancellationToken);
            var request = JsonSerializer.SerializeToUtf8Bytes(new { token = host.Registration.Token, request = new { command = "inspect" } });
            var length = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, request.Length);
            await client.WriteAsync(length, TestContext.Current.CancellationToken);
            await client.WriteAsync(request, TestContext.Current.CancellationToken);
            await Task.Delay(200, TestContext.Current.CancellationToken);
            var timer = Stopwatch.StartNew();
            await host.DisposeAsync();
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(4), $"Closing took {timer.Elapsed}");
        }
        Assert.Empty((await RunAsync("session", "list", "--session-dir", Sessions)).Json.GetProperty("result").GetProperty("sessions").EnumerateArray());
    }

    [Fact]
    public async Task AChangedPackIsReportedBeforeItIsCompiled()
    {
        var (host, _) = await StartSessionAsync();
        await using var _1 = host;
        // A broken manifest: compiling it would fail, but the hash check comes first.
        File.WriteAllText(Path.Combine(root, "mini", "manifest.json"), "{ not a manifest");
        var run = await RunAsync("session", "resolve", "--session-dir", Sessions);
        Assert.Equal(CliApp.IoFailure, run.ExitCode);
        Assert.Contains(Diagnostics(run, false), diagnostic => diagnostic.Code == "session-pack-changed");
    }

    [Fact]
    public async Task SinceRevisionNeedsAPositiveWait()
    {
        var run = await RunAsync("session", "inspect", "--wait", "0", "--since-revision", "0", "--session-dir", Sessions);
        Assert.Equal(CliApp.InvalidInput, run.ExitCode);
        Assert.Contains(Diagnostics(run, false), diagnostic => diagnostic.Code == "session-request-invalid");
    }
}
