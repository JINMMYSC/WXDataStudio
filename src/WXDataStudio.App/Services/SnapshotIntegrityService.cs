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
        }

        if (!manifest.Files.Any(x => x.Name == "EnMicroMsg.db"))
            issues.Add("EnMicroMsg.db is missing from the snapshot manifest.");

        return issues;
    }
}
