using System.IO;
using WXDataStudio.App.Models;

namespace WXDataStudio.App.Services;

public sealed class DatabaseInspectorService
{
    private static readonly byte[] SqliteHeader = "SQLite format 3\0"u8.ToArray();

    public async Task<DatabaseInspection> InspectAsync(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Database file not found.", path);

        var info = new FileInfo(path);
        var header = new byte[Math.Min(64, (int)Math.Min(info.Length, 64))];
        await using var stream = File.OpenRead(path);
        _ = await stream.ReadAsync(header);

        var plain = header.Length >= SqliteHeader.Length &&
            header.AsSpan(0, SqliteHeader.Length).SequenceEqual(SqliteHeader);
        return new DatabaseInspection
        {
            Path = path,
            Size = info.Length,
            IsPlainSqlite = plain,
            AppearsEncrypted = !plain && info.Length > 4096,
            HeaderHex = Convert.ToHexString(header).ToLowerInvariant(),
            Status = plain ? "Plain SQLite" : "Encrypted or non-standard SQLite header"
        };
    }
}
