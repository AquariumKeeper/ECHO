using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CharacterPlanner.Application.Sessions;

namespace CharacterPlanner.Infrastructure.Sessions;

/// <summary>
/// The live-session wire: one request and one reply per connection, each a 4-byte little-endian length then UTF-8
/// JSON. A request is <c>{"token": ..., "request": {...}}</c>; the reply is the endpoint's. Pipes are restricted to the
/// current user on both ends (<see cref="PipeOptions.CurrentUserOnly"/>), and the token, compared in constant time,
/// proves the client read this window's own registration.
/// </summary>
public static class SessionPipe
{
    /// <summary>A reply carries a build document of up to BuildDocument.MaxBytes plus history; requests are smaller.</summary>
    public const int MaximumMessage = 16 * 1024 * 1024;
    public const string Unauthorized = "session-unauthorized";

    internal static async Task WriteAsync(Stream stream, byte[] message, CancellationToken cancellationToken)
    {
        if (message.Length > MaximumMessage) { throw new InvalidDataException("The session message is too large"); }
        var length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, message.Length);
        await stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<byte[]> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var length = new byte[4];
        await stream.ReadExactlyAsync(length, cancellationToken).ConfigureAwait(false);
        var size = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (size < 0 || size > MaximumMessage) { throw new InvalidDataException("The session message has an invalid length"); }
        var message = new byte[size];
        await stream.ReadExactlyAsync(message, cancellationToken).ConfigureAwait(false);
        return message;
    }

    public static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>Sends one request to a registered window and returns its reply.</summary>
    public static async Task<JsonDocument> SendAsync(SessionRegistration registration, JsonObject request, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(request);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        var client = new NamedPipeClientStream(".", registration.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using (client.ConfigureAwait(false))
        {
            await client.ConnectAsync(limit.Token).ConfigureAwait(false);
            var envelope = new JsonObject { ["token"] = registration.Token, ["request"] = request };
            await WriteAsync(client, JsonSerializer.SerializeToUtf8Bytes(envelope), limit.Token).ConfigureAwait(false);
            return JsonDocument.Parse(await ReadAsync(client, limit.Token).ConfigureAwait(false));
        }
    }
}

/// <summary>
/// Serves one window's <see cref="SessionEndpoint"/> on a private named pipe and publishes the window in the registry.
/// One accept loop hands each connection to its own task, so a client waiting on a change never keeps others out;
/// at most <see cref="MaximumConnections"/> are served at once. No client's failure stops the window serving.
/// Disposing removes the registration first, then stops serving within a bounded time and never throws.
/// </summary>
public sealed class SessionPipeHost : IAsyncDisposable
{
    /// <summary>Concurrent connections, waiting inspects included; the next client waits for one to finish.</summary>
    public const int MaximumConnections = 64;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);
    private readonly SessionEndpoint endpoint;
    private readonly byte[] token;
    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim slots = new(MaximumConnections);
    private readonly Lock gate = new();
    private readonly HashSet<Task> connections = [];
    private Task accepting = Task.CompletedTask;
    private int disposed;

    public SessionRegistration Registration { get; }
    public string RegistryDirectory { get; }

    private SessionPipeHost(SessionEndpoint endpoint, SessionRegistration registration, string registryDirectory)
    {
        this.endpoint = endpoint;
        Registration = registration;
        RegistryDirectory = registryDirectory;
        token = Encoding.UTF8.GetBytes(registration.Token);
    }

    /// <summary>Starts serving, then publishes the registration, so a listed window is already reachable.
    /// Registrations left by windows whose process has ended are removed first.</summary>
    public static async Task<SessionPipeHost> StartAsync(PlannerSession session, string packDirectory, string packManifestSha256, string registryDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var id = Guid.NewGuid().ToString("N");
        var pack = session.Pack.Pack;
        var registration = new SessionRegistration(id, Environment.ProcessId, DateTimeOffset.UtcNow, pack.ID, pack.Version,
            Path.GetFullPath(packDirectory), packManifestSha256, "CharacterPlanner-" + id, SessionPipe.NewToken());
        var host = new SessionPipeHost(new SessionEndpoint(session), registration, Path.GetFullPath(registryDirectory));
        host.accepting = Task.Run(() => host.AcceptAsync(host.stopping.Token), CancellationToken.None);
        try
        {
            await SessionRegistry.PruneAsync(host.RegistryDirectory, cancellationToken).ConfigureAwait(false);
            await SessionRegistry.PublishAsync(host.RegistryDirectory, registration, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await host.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return host;
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await slots.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(Registration.Pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                slots.Release();
                if (server is not null) { await server.DisposeAsync().ConfigureAwait(false); }
                if (cancellationToken.IsCancellationRequested) { return; }
                // A pipe that can't be created right now (resources, a race at shutdown): try again shortly.
                try { await Task.Delay(100, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }
            var connection = ServeAsync(server, cancellationToken);
            lock (gate) { connections.Add(connection); }
            _ = connection.ContinueWith(done => { lock (gate) { connections.Remove(done); } }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>Answers one connection. Every failure ends only this connection; the task never faults.</summary>
    private async Task ServeAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            await using (server.ConfigureAwait(false)) { await AnswerAsync(server, cancellationToken).ConfigureAwait(false); }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
        finally { slots.Release(); }
    }

    private async Task AnswerAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(RequestTimeout);
        var message = await SessionPipe.ReadAsync(server, limit.Token).ConfigureAwait(false);
        byte[] reply;
        using (var envelope = JsonDocument.Parse(message))
        {
            var root = envelope.RootElement;
            if (Authorized(root) && root.TryGetProperty("request", out var request))
            {
                // A wait may outlast the read timeout; the endpoint bounds it itself.
                reply = await endpoint.HandleAsync(JsonSerializer.SerializeToUtf8Bytes(request), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                reply = JsonSerializer.SerializeToUtf8Bytes(new { ok = false, error = new { code = SessionPipe.Unauthorized, message = "The request did not carry this window's token" } });
            }
        }
        using var closing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        closing.CancelAfter(RequestTimeout);
        await SessionPipe.WriteAsync(server, reply, closing.Token).ConfigureAwait(false);
        // Wait for the client to close after reading, so the reply isn't cut off; a client that never does is dropped.
        _ = await server.ReadAsync(new byte[1], closing.Token).ConfigureAwait(false);
    }

    private bool Authorized(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("token", out var presented) || presented.ValueKind != JsonValueKind.String) { return false; }
        // A lone surrogate can't be text; such a token is simply wrong.
        try { return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented.GetString()!), token); }
        catch (InvalidOperationException) { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) { return; }
        try { SessionRegistry.Remove(RegistryDirectory, Registration.SessionId); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        await stopping.CancelAsync().ConfigureAwait(false);
        Task[] running;
        lock (gate) { running = [accepting, .. connections]; }
        // Connections end on cancellation; one stuck in the OS still can't hold up shutdown past the limit.
        try { await Task.WhenAll(running).WaitAsync(StopTimeout).ConfigureAwait(false); }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException) { }
    }
}
