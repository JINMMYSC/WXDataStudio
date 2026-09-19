using System.IO;
using System.Security.Cryptography;

namespace WXDataStudio.App.Services;

/// <summary>
/// Disposable working copy of a snapshot's database set.
///
/// SQLite refreshes shared-memory bookkeeping inside the -shm file of a WAL
/// database even when the connection is opened read-only. Reading a pristine
/// snapshot directly therefore rewrote snapshot bytes and broke the SHA-256
/// manifest. All database reads go through this copy instead, so the snapshot
/// directory stays byte-for-byte identical.
/// </summary>
public sealed class SnapshotReadSession : IAsyncDisposable
{
    private static readonly string[] DatabaseFileNames =
    {
        "EnMicroMsg.db",
        "EnMicroMsg.db-wal",
        "EnMicroMsg.db-shm"
    };

    private SnapshotReadSession(
        string snapshotDirectory,
        string workingDirectory,
        IReadOnlyList<string> copiedFiles)
    {
        SnapshotDirectory = snapshotDirectory;
        WorkingDirectory = workingDirectory;
        CopiedFiles = copiedFiles;
        DatabasePath = Path.Combine(workingDirectory, "EnMicroMsg.db");
    }

    public string SnapshotDirectory { get; }
    public string WorkingDirectory { get; }
    public string DatabasePath { get; }
    public IReadOnlyList<string> CopiedFiles { get; }

    public string DerivedDirectory => Path.Combine(SnapshotDirectory, "derived");

    public static string WorkingDirectoryFor(string snapshotDirectory) =>
        Path.Combine(snapshotDirectory, "derived", "read-session");

    public static async Task<SnapshotReadSession> OpenAsync(
        string snapshotDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(snapshotDirectory))
            throw new ArgumentException("Snapshot directory is required.", nameof(snapshotDirectory));
        if (!Directory.Exists(snapshotDirectory))
            throw new DirectoryNotFoundException(snapshotDirectory);

        var workingDirectory = WorkingDirectoryFor(snapshotDirectory);
        TryDelete(workingDirectory);
        Directory.CreateDirectory(workingDirectory);

        var copied = new List<string>();
        foreach (var name in DatabaseFileNames)
        {
            var source = Path.Combine(snapshotDirectory, name);
            if (!File.Exists(source)) continue;
            await CopyVerifiedAsync(source, Path.Combine(workingDirectory, name), cancellationToken);
            copied.Add(name);
        }

        if (!copied.Contains("EnMicroMsg.db"))
            throw new FileNotFoundException(
                "Snapshot database is missing.",
                Path.Combine(snapshotDirectory, "EnMicroMsg.db"));

        return new SnapshotReadSession(snapshotDirectory, workingDirectory, copied);
    }

    public ValueTask DisposeAsync()
    {
        TryDelete(WorkingDirectory);
        return ValueTask.CompletedTask;
    }

    private static async Task CopyVerifiedAsync(
        string source,
        string target,
        CancellationToken cancellationToken)
    {
        const int bufferSize = 1024 * 1024;
        await using (var input = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (var output = File.Open(target, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await input.CopyToAsync(output, bufferSize, cancellationToken);
        }

        var expected = await HashAsync(source, cancellationToken);
        var actual = await HashAsync(target, cancellationToken);
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Snapshot working copy verification failed for {Path.GetFileName(source)}.");
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDelete(string directory)
    {
        if (!Directory.Exists(directory)) return;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(directory, true);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException) when (attempt < 2)
            {
                Thread.Sleep(50);
            }
            catch
            {
                return;
            }
        }
    }
}
