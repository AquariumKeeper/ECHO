using System.Collections.Immutable;
using System.Text.Json;
using CharacterPlanner.Application.Assumptions;

namespace CharacterPlanner.Infrastructure.Storage;

/// <summary>
/// Assumption sets in one JSON file of the app's own versioned format (<see cref="Format"/>, <see cref="Version"/>);
/// the Go app's file is never read or written. Every write goes to a temporary file beside it that then replaces it in
/// one move, so a crash leaves the old sets or the new ones, never a partial file. A file that can't be read is
/// reported damaged and never written over, so a save can't silently discard the sets in it.
/// </summary>
/// <remarks>
/// Writers hold an exclusive lock on the file's <see cref="LockSuffix"/> companion from reading the sets to their move,
/// so two stores on one file, in one process or two, can't lose each other's save. Readers take no lock.
/// </remarks>
public sealed class FileAssumptionSetStore(string path, TimeSpan? lockTimeout = null) : IAssumptionSetStore
{
    public const string Format = "character-planner-assumption-sets";
    public const int Version = 1;
    public const string LockSuffix = ".lock";
    /// <summary>A larger file isn't one this store wrote; it is reported damaged rather than read into memory.</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    private readonly string file = Path.GetFullPath(path);
    private readonly TimeSpan lockWait = lockTimeout ?? FileBuildLibrary.DefaultLockTimeout;

    public string FilePath => file;

    public async Task<ImmutableArray<AssumptionSet>> ListAsync(string packId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packId);
        var sets = await ReadAsync(cancellationToken).ConfigureAwait(false);
        return [.. sets.Where(set => set.PackId == packId).OrderBy(set => set.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<AssumptionSet> SaveAsync(string packId, string name, ImmutableDictionary<string, string> values, bool replace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packId);
        ArgumentNullException.ThrowIfNull(values);
        name = (name ?? "").Trim();
        if (name.Length == 0) { throw new AssumptionSetException(AssumptionSetErrorCodes.NameRequired, "an assumption set needs a name"); }
        var set = new AssumptionSet(packId, name, values.WithComparers(StringComparer.Ordinal));
        await using var held = await LockAsync(cancellationToken).ConfigureAwait(false);
        var sets = await ReadAsync(cancellationToken).ConfigureAwait(false);
        var index = Find(sets, packId, name);
        if (index < 0) { sets = sets.Add(set); }
        else if (!replace) { throw new AssumptionSetException(AssumptionSetErrorCodes.Exists, $"an assumption set named \"{sets[index].Name}\" already exists"); }
        else { sets = sets.SetItem(index, set); }
        await WriteAsync(sets, cancellationToken).ConfigureAwait(false);
        return set;
    }

    public async Task DeleteAsync(string packId, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packId);
        ArgumentNullException.ThrowIfNull(name);
        await using var held = await LockAsync(cancellationToken).ConfigureAwait(false);
        var sets = await ReadAsync(cancellationToken).ConfigureAwait(false);
        var index = Find(sets, packId, name.Trim());
        if (index < 0) { throw new AssumptionSetException(AssumptionSetErrorCodes.NotFound, $"no assumption set named \"{name}\""); }
        await WriteAsync(sets.RemoveAt(index), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The pack's set with this name, ignoring case, or -1.</summary>
    private static int Find(ImmutableArray<AssumptionSet> sets, string packId, string name)
    {
        for (var index = 0; index < sets.Length; index++)
        {
            if (sets[index].PackId == packId && string.Equals(sets[index].Name, name, StringComparison.OrdinalIgnoreCase)) { return index; }
        }
        return -1;
    }

    /// <summary>Every stored set, of every pack; none when there is no file yet.</summary>
    private async Task<ImmutableArray<AssumptionSet>> ReadAsync(CancellationToken cancellationToken)
    {
        byte[] bytes;
        FileStream stream;
        try { stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous); }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return []; }
        await using (stream.ConfigureAwait(false))
        {
            if (stream.Length > MaxBytes) { throw Damaged($"it is larger than {MaxBytes} bytes", null); }
            bytes = new byte[stream.Length];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        try { return Decode(bytes); }
        catch (JsonException exception) { throw Damaged(exception.Message, exception); }
        catch (InvalidOperationException exception) { throw Damaged(exception.Message, exception); }
    }

    private AssumptionSetException Damaged(string why, Exception? inner) =>
        new(AssumptionSetErrorCodes.Damaged, $"The saved assumption sets at {file} can't be read: {why}", inner);

    /// <summary>Reads the file strictly: the right format and version, and every set with a pack, a name and text values.</summary>
    private static ImmutableArray<AssumptionSet> Decode(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) { throw new JsonException("the file is not a JSON object"); }
        if (!root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != Format)
        { throw new JsonException($"it is not a \"{Format}\" file"); }
        if (!root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var number) || number != Version)
        { throw new JsonException($"its version isn't {Version}"); }
        if (!root.TryGetProperty("sets", out var sets) || sets.ValueKind != JsonValueKind.Array) { throw new JsonException("it has no \"sets\" array"); }
        var decoded = ImmutableArray.CreateBuilder<AssumptionSet>();
        foreach (var set in sets.EnumerateArray())
        {
            var packId = Text(set, "packId");
            var name = Text(set, "name");
            if (packId.Trim().Length == 0 || name.Trim().Length == 0) { throw new JsonException("an assumption set needs a pack and a name"); }
            if (!set.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Object) { throw new JsonException($"assumption set \"{name}\" has no \"values\" object"); }
            var read = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            foreach (var value in values.EnumerateObject())
            {
                if (value.Value.ValueKind != JsonValueKind.String) { throw new JsonException($"assumption set \"{name}\"'s value \"{value.Name}\" isn't text"); }
                read[value.Name] = value.Value.GetString()!;
            }
            decoded.Add(new(packId, name, read.ToImmutable()));
        }
        return decoded.ToImmutable();
    }

    private static string Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new JsonException($"an assumption set has no \"{property}\" text");

    private static byte[] Encode(ImmutableArray<AssumptionSet> sets)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("format", Format);
            writer.WriteNumber("version", Version);
            writer.WriteStartArray("sets");
            foreach (var set in sets)
            {
                writer.WriteStartObject();
                writer.WriteString("packId", set.PackId);
                writer.WriteString("name", set.Name);
                writer.WriteStartObject("values");
                foreach (var (id, value) in set.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal)) { writer.WriteString(id, value); }
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>Takes the file's writer lock, retrying while another writer holds it.</summary>
    private Task<FileStream> LockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        return AtomicFile.LockAsync(file + LockSuffix, lockWait,
            exception => new AssumptionSetException(AssumptionSetErrorCodes.Busy, $"Another program is saving assumption sets to {file}", exception), cancellationToken);
    }

    /// <summary>Writes the sets durably and moves them over the file.</summary>
    private Task WriteAsync(ImmutableArray<AssumptionSet> sets, CancellationToken cancellationToken) =>
        AtomicFile.WriteAsync(file, Encode(sets), replace: true, lockWait,
            exception => new AssumptionSetException(AssumptionSetErrorCodes.Busy, $"The assumption sets at {file} are in use by another program or can't be written", exception),
            cancellationToken);
}
