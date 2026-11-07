namespace CharacterPlanner.Infrastructure.Storage;

/// <summary>
/// The file stores' shared writing: a writer lock file, and durable replacement through a temporary file. One copy, so
/// a fix (Windows' move retries, the lock wait) reaches the build library and the assumption sets alike.
/// </summary>
internal static class AtomicFile
{
    private static readonly TimeSpan Retry = TimeSpan.FromMilliseconds(25);

    /// <summary>Takes a writer lock, retrying while another writer holds it; past the wait, <paramref name="busy"/> says why.</summary>
    public static async Task<FileStream> LockAsync(string lockPath, TimeSpan wait, Func<IOException, Exception> busy, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(Retry, cancellationToken).ConfigureAwait(false); }
            catch (IOException exception) { throw busy(exception); }
        }
    }

    /// <summary>
    /// Writes the bytes durably to a temporary file beside <paramref name="path"/> (hidden by its dot and .tmp
    /// extension), then moves it into place, and returns the temporary file's write time. Windows won't replace a file
    /// that anything has open, even sharing delete, and readers (a store's own, an antivirus scan, a sync client) hold
    /// one only briefly, so a replacing move is retried for as long as a writer waits for the lock; past that,
    /// <paramref name="busy"/> says why.
    /// </summary>
    public static async Task<DateTimeOffset> WriteAsync(string path, byte[] bytes, bool replace, TimeSpan wait,
        Func<Exception, Exception> busy, CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            DateTimeOffset written;
            var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                // On disk before the move, so a crash can't leave an empty file in place.
                stream.Flush(flushToDisk: true);
                written = new DateTimeOffset(File.GetLastWriteTimeUtc(stream.SafeFileHandle), TimeSpan.Zero);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var deadline = DateTime.UtcNow + wait;
            while (true)
            {
                try { File.Move(temporary, path, overwrite: replace); return written; }
                catch (Exception exception) when (replace && exception is IOException or UnauthorizedAccessException)
                {
                    if (DateTime.UtcNow >= deadline) { throw busy(exception); }
                    await Task.Delay(Retry, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // Best effort: a failed cleanup mustn't hide the write's own outcome.
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
