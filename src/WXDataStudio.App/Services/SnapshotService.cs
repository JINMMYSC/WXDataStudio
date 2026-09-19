using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
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

            await CaptureResolverSupportAsync(root, pulled, log);

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

    private async Task CaptureResolverSupportAsync(
        string snapshotRoot,
        List<SnapshotFile> captured,
        Action<string>? log)
    {
        var supportRoot = Path.Combine(snapshotRoot, "support");
        Directory.CreateDirectory(supportRoot);

        var privateFiles = new[]
        {
            (Remote: "/data/user/0/com.tencent.mm/shared_prefs/auth_info_key_prefs.xml", Local: "auth_info_key_prefs.xml"),
            (Remote: "/data/user/0/com.tencent.mm/shared_prefs/system_config_prefs.xml", Local: "system_config_prefs.xml"),
            (Remote: "/data/user/0/com.tencent.mm/shared_prefs/com.tencent.mm_preferences.xml", Local: "com.tencent.mm_preferences.xml"),
            (Remote: "/data/user/0/com.tencent.mm/MicroMsg/CompatibleInfo.cfg", Local: "CompatibleInfo.cfg")
        };

        foreach (var item in privateFiles)
        {
            try
            {
                var exists = await _adb.RootShellAsync($"test -f '{item.Remote}'");
                if (!exists.Success) continue;
                var local = Path.Combine(supportRoot, item.Local);
                var pull = await _adb.RootPullFileAsync(item.Remote, local);
                if (!pull.Success || !File.Exists(local) || new FileInfo(local).Length == 0) continue;
                await AddManifestFileAsync(snapshotRoot, local, captured);
            }
            catch
            {
                // Resolver support is best-effort and must never invalidate a valid DB snapshot.
            }
        }

        var tokenCommands = new Dictionary<string, string>
        {
            ["persist.radio.imei"] = "getprop persist.radio.imei",
            ["ro.ril.oem.imei"] = "getprop ro.ril.oem.imei",
            ["ril.gsm.imei"] = "getprop ril.gsm.imei",
            ["android_id"] = "settings get secure android_id",
            ["ro.serialno"] = "getprop ro.serialno"
        };
        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in tokenCommands)
        {
            try
            {
                var result = await _adb.ShellAsync(item.Value);
                var value = result.StdOut.Trim();
                if (result.Success && !string.IsNullOrWhiteSpace(value) &&
                    !value.Equals("null", StringComparison.OrdinalIgnoreCase))
                    tokens[item.Key] = value;
            }
            catch
            {
                // Keep support capture best-effort.
            }
        }

        if (tokens.Count > 0)
        {
            var tokenPath = Path.Combine(supportRoot, "device_tokens.json");
            await File.WriteAllTextAsync(tokenPath,
                JsonSerializer.Serialize(tokens, new JsonSerializerOptions { WriteIndented = true }));
            await AddManifestFileAsync(snapshotRoot, tokenPath, captured);
        }

        if (captured.Any(x => x.Name.StartsWith("support/", StringComparison.OrdinalIgnoreCase)))
            log?.Invoke("Resolver support captured for offline key diagnostics (values are not logged).");
    }

    private static async Task AddManifestFileAsync(
        string snapshotRoot,
        string localPath,
        List<SnapshotFile> captured)
    {
        var bytes = await File.ReadAllBytesAsync(localPath);
        if (bytes.LongLength == 0) return;
        var relative = Path.GetRelativePath(snapshotRoot, localPath).Replace('\\', '/');
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        captured.Add(new SnapshotFile(relative, bytes.LongLength, hash));
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
