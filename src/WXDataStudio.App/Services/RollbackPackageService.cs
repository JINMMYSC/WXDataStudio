using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace WXDataStudio.App.Services;

public sealed record RollbackPackageResult(
    string PackagePath,
    long Size,
    string Sha256);

public sealed class RollbackPackageService
{
    private readonly SnapshotIntegrityService _integrity = new();

    public async Task<RollbackPackageResult> CreateAsync(
        string snapshotDirectory,
        string? outputRoot = null)
    {
        var issues = await _integrity.CheckAsync(snapshotDirectory);
        if (issues.Count > 0)
            throw new InvalidDataException("Snapshot integrity check failed: " + string.Join("; ", issues));

        outputRoot ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "WXDataStudio", "rollback-packages");
        Directory.CreateDirectory(outputRoot);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var packagePath = Path.Combine(outputRoot, $"rollback-{stamp}.zip");
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "manifest.json", "EnMicroMsg.db", "EnMicroMsg.db-wal", "EnMicroMsg.db-shm" })
            {
                var path = Path.Combine(snapshotDirectory, name);
                if (File.Exists(path))
                    archive.CreateEntryFromFile(path, name, CompressionLevel.Optimal);
            }
        }

        var bytes = await File.ReadAllBytesAsync(packagePath);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new RollbackPackageResult(packagePath, bytes.LongLength, sha256);
    }
}
