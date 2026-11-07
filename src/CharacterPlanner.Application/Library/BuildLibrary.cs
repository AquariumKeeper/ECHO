using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;

namespace CharacterPlanner.Application.Library;

/// <summary>
/// A saved build as listed. <paramref name="Version"/> identifies the stored bytes, so a save or delete can say
/// which version it expects to replace. A damaged record is still listed, with <paramref name="Problem"/> saying
/// why it can't be opened, so it is reported rather than silently skipped or overwritten.
/// </summary>
public sealed record LibraryEntry(string Id, string Name, string PackId, string PackVersion, DateTimeOffset SavedAt, string Version, string? Problem = null);

/// <summary>A build read from the library, with the version a later save must name to replace it.</summary>
public sealed record LibraryBuild(string Id, BuildSnapshot Build, string Version, DateTimeOffset SavedAt);

/// <summary>Stable library error codes. Messages are for people and may change; codes may not.</summary>
public static class LibraryErrorCodes
{
    public const string NotFound = "library-not-found";
    public const string Damaged = "library-damaged";
    /// <summary>The record changed (or appeared, or went) since the caller read it.</summary>
    public const string Conflict = "library-conflict";
    public const string InvalidId = "library-invalid-id";
    public const string Unsaveable = "library-unsaveable";
    /// <summary>Another writer held the library for too long.</summary>
    public const string Busy = "library-busy";
}

public sealed class LibraryException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>
/// The user's saved builds (C4, ledger F52). Saving never replaces a record silently: a new build gets a new id,
/// and replacing or deleting a record names the version that was read, so an edit made elsewhere in between, by
/// this program or another one on the same library, is a conflict rather than lost. Failures callers can act on
/// are <see cref="LibraryException"/>s with a stable code; other I/O failures stay exceptions.
/// </summary>
public interface IBuildLibrary
{
    /// <summary>Every record, damaged or unreadable ones included with their <see cref="LibraryEntry.Problem"/>, by name.</summary>
    Task<ImmutableArray<LibraryEntry>> ListAsync(CancellationToken cancellationToken = default);

    /// <exception cref="LibraryException"><see cref="LibraryErrorCodes.NotFound"/>, <see cref="LibraryErrorCodes.Damaged"/> or <see cref="LibraryErrorCodes.InvalidId"/>.</exception>
    Task<LibraryBuild> LoadAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Saves a new record and returns it with its new id.</summary>
    /// <exception cref="LibraryException"><see cref="LibraryErrorCodes.Unsaveable"/> or <see cref="LibraryErrorCodes.Busy"/>.</exception>
    Task<LibraryBuild> CreateAsync(BuildSnapshot build, CancellationToken cancellationToken = default);

    /// <summary>Replaces record <paramref name="id"/>, which must still be at <paramref name="expectedVersion"/>.</summary>
    /// <exception cref="LibraryException"><see cref="LibraryErrorCodes.Conflict"/>, <see cref="LibraryErrorCodes.Unsaveable"/>, <see cref="LibraryErrorCodes.InvalidId"/> or <see cref="LibraryErrorCodes.Busy"/>.</exception>
    Task<LibraryBuild> ReplaceAsync(string id, BuildSnapshot build, string expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>Deletes record <paramref name="id"/>, which must still be at <paramref name="expectedVersion"/>.</summary>
    /// <exception cref="LibraryException"><see cref="LibraryErrorCodes.Conflict"/>, <see cref="LibraryErrorCodes.InvalidId"/> or <see cref="LibraryErrorCodes.Busy"/>.</exception>
    Task DeleteAsync(string id, string expectedVersion, CancellationToken cancellationToken = default);
}
