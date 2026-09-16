using System.IO;
using System.Collections.Concurrent;
using WXDataStudio.App.Models;

namespace WXDataStudio.App.Services;

public sealed class MediaLocatorService
{
    private readonly AdbService _adb;
    private readonly ConcurrentDictionary<string, MediaResolution> _cache = new();

    public MediaLocatorService(AdbService adb)
    {
        _adb = adb;
    }

    public async Task<MediaResolution> ResolveAsync(DeviceInfo device, WeChatMessage message)
    {
        var cacheKey = $"{device.Serial}:{message.LocalId}:{message.ImgPath}";
        if (_cache.TryGetValue(cacheKey, out var cached)) return cached;
        var root = device.ExternalMediaRoot;
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(message.ImgPath))
            return Cache(cacheKey, new MediaResolution { MessageId = message.LocalId, Kind = message.Kind });

        var token = SanitizeToken(message.ImgPath);
        if (string.IsNullOrWhiteSpace(token))
            return Cache(cacheKey, new MediaResolution { MessageId = message.LocalId, Kind = message.Kind });

        var folders = FoldersFor(message.Kind);
        var found = new List<MediaCandidate>();
        foreach (var folder in folders)
            found.AddRange(await SearchFolderAsync(root, folder, token));

        return Cache(cacheKey, new MediaResolution
        {
            MessageId = message.LocalId,
            Kind = message.Kind,
            Candidates = found.DistinctBy(x => x.RemotePath).Take(20).ToArray()
        });
    }

    private async Task<IEnumerable<MediaCandidate>> SearchFolderAsync(
        string root, string folder, string token)
    {
        var dir = $"{root}/{folder}";
        var quoted = token.Replace("'", "", StringComparison.Ordinal);
        var result = await _adb.ShellAsync(
            $"find '{dir}' -type f -iname '*{quoted}*' 2>/dev/null | head -n 8");
        if (!result.Success && string.IsNullOrWhiteSpace(result.StdOut))
            return Array.Empty<MediaCandidate>();
        return result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => new MediaCandidate(x.Trim(), folder, true))
            .Where(x => !string.IsNullOrWhiteSpace(x.RemotePath))
            .ToArray();
    }

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

    private MediaResolution Cache(string key, MediaResolution value)
    {
        _cache[key] = value;
        return value;
    }
}
