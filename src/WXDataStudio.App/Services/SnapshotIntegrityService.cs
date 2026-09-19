using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace WXDataStudio.App.Services;

public sealed class SnapshotIntegrityService
{
    public async Task<IReadOnlyList<string>> CheckAsync(string snapshotDirectory)
    {
        var issues = new List<string>();
        var manifestPath = Path.Combine(snapshotDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
            return new[] { "manifest.json is missing." };

        var json = await File.ReadAllTextAsync(manifestPath);
        var manifest = JsonSerializer.Deserialize<SnapshotManifest>(json);
        if (manifest is null)
            return new[] { "manifest.json could not be parsed." };

        foreach (var item in manifest.Files)
        {
            var path = Path.Combine(snapshotDirectory, item.Name);
            if (!File.Exists(path))
            {
                issues.Add($"Missing file: {item.Name}");
                continue;
            }

            var bytes = await File.ReadAllBytesAsync(path);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!hash.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                issues.Add($"SHA-256 mismatch: {item.Name}");
            if (bytes.LongLength != item.Size)
                issues.Add($"Size mismatch: {item.Name}");

            // A hash manifest can faithfully hash a DB already corrupted during adb
            // transfer. Validate known SQLite/WAL structural alignment as well.
            if (item.Name.Equals("EnMicroMsg.db", StringComparison.OrdinalIgnoreCase) &&
                bytes.Length >= 16 && bytes.Length % 1024 != 0 &&
                !bytes.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8))
                issues.Add("Encrypted EnMicroMsg.db is not aligned to 1024-byte pages.");

            if (item.Name.Equals("EnMicroMsg.db-wal", StringComparison.OrdinalIgnoreCase))
            {
                if (bytes.Length < 32)
                    issues.Add("WAL header is incomplete.");
                else
                {
                    var pageSize = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4));
                    if (pageSize is < 512 or > 65536 || (pageSize & (pageSize - 1)) != 0 ||
                        (bytes.LongLength - 32) % (pageSize + 24L) != 0)
                        issues.Add("WAL frame alignment is invalid.");
                }
            }

            if (item.Name.Equals("EnMicroMsg.db-shm", StringComparison.OrdinalIgnoreCase) &&
                bytes.Length % 32768 != 0)
                issues.Add("SHM file alignment is invalid.");
        }

        if (!manifest.Files.Any(x => x.Name == "EnMicroMsg.db"))
            issues.Add("EnMicroMsg.db is missing from the snapshot manifest.");

        return issues;
    }
}