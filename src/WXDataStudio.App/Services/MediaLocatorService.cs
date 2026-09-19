using System.IO;
using System.Collections.Concurrent;
using WXDataStudio.App.Models;

namespace WXDataStudio.App.Services;

public sealed class MediaLocatorService
{
    private readonly AdbService _adb;
    private readonly ConcurrentDictionary<MediaCacheKey, MediaResolution> _cache = new();

    public MediaLocatorService(AdbService adb)
    {
        _adb = adb;
    }

    public async Task<MediaResolution> ResolveAsync(DeviceInfo device, WeChatMessage message)
    {
        var cacheKey = new MediaCacheKey(
            device.Serial,
            device.AccountDirectory,
            device.ExternalAccountDirectory,
            message.Kind,
            message.LocalId,
            message.ImgPath);
        if (_cache.TryGetValue(cacheKey, out var cached)) return cached;
        if (string.IsNullOrWhiteSpace(message.ImgPath))
            return Cache(cacheKey, new MediaResolution { MessageId = message.LocalId, Kind = message.Kind });

        var token = SanitizeToken(message.ImgPath);
        if (string.IsNullOrWhiteSpace(token))
            return Cache(cacheKey, new MediaResolution { MessageId = message.LocalId, Kind = message.Kind });

        var found = new List<MediaCandidate>();
        foreach (var location in GetSearchLocations(device, message.Kind))
            found.AddRange(await SearchFolderAsync(location, token));

        return Cache(cacheKey, new MediaResolution
        {
            MessageId = message.LocalId,
            Kind = message.Kind,
            Candidates = found.DistinctBy(x => x.RemotePath).Take(20).ToArray()
        });
    }

    private async Task<IEnumerable<MediaCandidate>> SearchFolderAsync(
        MediaSearchLocation location, string token)
    {
        var quoted = token.Replace("'", "", StringComparison.Ordinal);
        var command =
            $"find '{location.Directory}' -type f -iname '*{quoted}*' 2>/dev/null | head -n 8";
        var result = location.RequiresRoot
            ? await _adb.RootShellAsync(command)
            : await _adb.ShellAsync(command);
        if (!result.Success && string.IsNullOrWhiteSpace(result.StdOut))
            return Array.Empty<MediaCandidate>();
        return result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => new MediaCandidate(x.Trim(), location.Role, true))
            .Where(x => !string.IsNullOrWhiteSpace(x.RemotePath))
            .ToArray();
    }

    public static IReadOnlyList<MediaSearchLocation> GetSearchLocations(
        DeviceInfo device, MessageKind kind)
    {
        var roots = new List<(string Path, bool RequiresRoot)>(2);
        if (IsSafeAccountDirectory(device.AccountDirectory))
            roots.Add(($"/data/user/0/com.tencent.mm/MicroMsg/{device.AccountDirectory}", true));
        if (IsSafeAccountDirectory(device.ExternalAccountDirectory))
            roots.Add((device.ExternalMediaRoot, false));

        return roots
            .SelectMany(root => FoldersFor(kind).Select(folder =>
                new MediaSearchLocation($"{root.Path}/{folder}", folder, root.RequiresRoot)))
            .DistinctBy(x => x.Directory, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsSafeAccountDirectory(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        value is not "." and not ".." &&
        value.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.');

    private static string[] FoldersFor(MessageKind kind) => kind switch
    {
        MessageKind.Image => ["image2"],
        MessageKind.Video => ["video"],
        MessageKind.Voice => ["voice2"],
        MessageKind.Emoji => ["emoji"],
        MessageKind.File => ["Download", "download"],
        _ => ["image2", "video", "voice2", "emoji"]
    };

    private static string SanitizeToken(string input)
    {
        var value = input.Replace("THUMBNAIL_DIRPATH://", "", StringComparison.OrdinalIgnoreCase);
        value = value.Replace("th_", "", StringComparison.OrdinalIgnoreCase);
        value = Path.GetFileNameWithoutExtension(value.Trim());
        return new string(value.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-').ToArray());
    }

    private MediaResolution Cache(MediaCacheKey key, MediaResolution value)
    {
        _cache[key] = value;
        return value;
    }

    private sealed record MediaCacheKey(
        string Serial,
        string AccountDirectory,
        string ExternalAccountDirectory,
        MessageKind Kind,
        long MessageId,
        string? ImagePath);
}

public sealed record MediaSearchLocation(
    string Directory,
    string Role,
    bool RequiresRoot);
