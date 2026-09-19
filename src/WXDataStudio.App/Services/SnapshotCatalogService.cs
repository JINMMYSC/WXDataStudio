using System.IO;
using System.Text.Json;

namespace WXDataStudio.App.Services;

public sealed record SnapshotCatalogItem(
    string DirectoryPath,
    DateTimeOffset CreatedAt,
    long DatabaseSize,
    bool HasWal,
    bool HasShm,
    bool IsUsable)
{
    public string DisplayName =>
        $"{CreatedAt:yyyy-MM-dd HH:mm:ss} · DB {DatabaseSize / 1024d / 1024d:F1} MB";
}

public sealed class SnapshotCatalogService
{
    public SnapshotCatalogService(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "WXDataStudio", "snapshots");
    }

    public string RootDirectory { get; }

    public async Task<IReadOnlyList<SnapshotCatalogItem>> ListAsync()
    {
        if (!Directory.Exists(RootDirectory)) return Array.Empty<SnapshotCatalogItem>();
        var items = new List<SnapshotCatalogItem>();
        foreach (var directory in Directory.GetDirectories(RootDirectory))
        {
            var item = await InspectDirectoryAsync(directory);
            if (item is not null) items.Add(item);
        }
        return items.OrderByDescending(x => x.CreatedAt).ToArray();
    }

    public async Task<SnapshotCatalogItem?> FindLatestUsableAsync() =>
        (await ListAsync()).FirstOrDefault(x => x.IsUsable);

    public async Task<SnapshotCatalogItem?> InspectDirectoryAsync(string directory)
    {
        if (!Directory.Exists(directory)) return null;
        var manifestPath = Path.Combine(directory, "manifest.json");
        var databasePath = Path.Combine(directory, "EnMicroMsg.db");
        if (!File.Exists(manifestPath) || !File.Exists(databasePath)) return null;

        SnapshotManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SnapshotManifest>(
                await File.ReadAllTextAsync(manifestPath));
        }
        catch
        {
            return null;
        }

        if (manifest is null) return null;
        var size = new FileInfo(databasePath).Length;
        return new SnapshotCatalogItem(
            directory,
            manifest.CreatedAt,
            size,
            File.Exists(Path.Combine(directory, "EnMicroMsg.db-wal")),
            File.Exists(Path.Combine(directory, "EnMicroMsg.db-shm")),
            size > 0 && manifest.Files.Any(x =>
                x.Name.Equals("EnMicroMsg.db", StringComparison.OrdinalIgnoreCase)));
    }
}
