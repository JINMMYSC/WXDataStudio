using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using WXDataStudio.App.Models;

namespace WXDataStudio.App.Services;

public sealed class SnapshotService
{
    private readonly AdbService _adb;

    public SnapshotService(AdbService adb)
    {
        _adb = adb;
    }

    public async Task<SnapshotResult> CreateDatabaseSnapshotAsync(
        DeviceInfo device,
        Action<string>? log = null)
    {
        if (!device.RootAvailable)
            throw new InvalidOperationException("Root is required for a consistent private-data snapshot.");
        if (string.IsNullOrWhiteSpace(device.AccountDirectory))
            throw new InvalidOperationException("No WeChat account directory containing EnMicroMsg.db was found.");

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "WXDataStudio", "snapshots", stamp);
        Directory.CreateDirectory(root);

        var sourceRoot = $"/data/user/0/com.tencent.mm/MicroMsg/{device.AccountDirectory}";
        var files = new[] { "EnMicroMsg.db", "EnMicroMsg.db-wal", "EnMicroMsg.db-shm" };
        var pulled = new List<SnapshotFile>();

        log?.Invoke("Stopping WeChat for a consistent read-only snapshot...");
        await _adb.ForceStopWeChatAsync();

        try
        {
            foreach (var name in files)
            {
                var source = $"{sourceRoot}/{name}";
                var exists = await _adb.RootShellAsync($"test -f '{source}'");
                if (!exists.Success) continue;

                var local = Path.Combine(root, name);
                var pull = await _adb.RootPullFileAsync(source, local);
                if (!pull.Success || !File.Exists(local))
                    throw new InvalidOperationException($"Failed to read {name} through root: {pull.StdErr}");

                var bytes = await File.ReadAllBytesAsync(local);
                if (bytes.LongLength == 0)
                    throw new InvalidOperationException($"Root pull returned an empty file: {name}");
                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                pulled.Add(new SnapshotFile(name, bytes.LongLength, hash));
                log?.Invoke($"Snapshot file: {name} ({bytes.LongLength:N0} bytes)");
            }

            if (pulled.Count == 0)
                throw new InvalidOperationException("No database files were captured.");

            var manifest = new SnapshotManifest
            {
                CreatedAt = DateTimeOffset.Now,
                Device = device,
                Files = pulled
            };

            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            await File.WriteAllTextAsync(Path.Combine(root, "manifest.json"), json);
            log?.Invoke("Snapshot completed with SHA-256 manifest.");
            return new SnapshotResult(root, pulled);
        }
        finally
        {
            await _adb.LaunchWeChatAsync();
        }
    }
}

public sealed record SnapshotResult(
    string DirectoryPath,
    IReadOnlyList<SnapshotFile> Files);

public sealed record SnapshotFile(
    string Name,
    long Size,
    string Sha256);

public sealed class SnapshotManifest
{
    public DateTimeOffset CreatedAt { get; init; }
    public DeviceInfo Device { get; init; } = new();
    public IReadOnlyList<SnapshotFile> Files { get; init; } = Array.Empty<SnapshotFile>();
}
