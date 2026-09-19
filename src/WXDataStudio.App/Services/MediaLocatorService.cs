using System.IO;
using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using WXDataStudio.App.Models;

namespace WXDataStudio.App.Services;

public sealed class MediaLocatorService
{
    private readonly AdbService _adb;
    private readonly ConcurrentDictionary<MediaCacheKey, Lazy<Task<MediaResolution>>> _cache = new();
    private readonly SemaphoreSlim _searchGate = new(2, 2);

    public MediaLocatorService(AdbService adb)
    {
        _adb = adb;
    }

    public Task<MediaResolution> ResolveAsync(DeviceInfo device, WeChatMessage message)
    {
        var tokens = GetSearchTokens(message);
        var cacheKey = new MediaCacheKey(
            device.Serial,
            device.AccountDirectory,
            device.ExternalAccountDirectory,
            message.Kind,
            message.LocalId,
            string.Join('\u001f', tokens));
        var lazy = _cache.GetOrAdd(cacheKey, _ => new Lazy<Task<MediaResolution>>(
            () => ResolveCoreAsync(device, message, tokens),
            LazyThreadSafetyMode.ExecutionAndPublication));
        return AwaitCachedAsync(cacheKey, lazy);
    }

    private async Task<MediaResolution> AwaitCachedAsync(
        MediaCacheKey cacheKey, Lazy<Task<MediaResolution>> lazy)
    {
        try
        {
            return await lazy.Value;
        }
        catch
        {
            _cache.TryRemove(new KeyValuePair<MediaCacheKey, Lazy<Task<MediaResolution>>>(
                cacheKey, lazy));
            throw;
        }
    }

    private async Task<MediaResolution> ResolveCoreAsync(
        DeviceInfo device,
        WeChatMessage message,
        IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0)
            return new MediaResolution { MessageId = message.LocalId, Kind = message.Kind };

        var searches = GetSearchLocations(device, message.Kind)
            .Select(location => SearchFolderAsync(location, tokens));
        var found = (await Task.WhenAll(searches)).SelectMany(x => x).ToArray();

        return new MediaResolution
        {
            MessageId = message.LocalId,
            Kind = message.Kind,
            Candidates = found.DistinctBy(x => x.RemotePath).Take(20).ToArray()
        };
    }

    private async Task<IEnumerable<MediaCandidate>> SearchFolderAsync(
        MediaSearchLocation location, IReadOnlyList<string> tokens)
    {
        static string Q(string value) =>
            "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
        var patterns = string.Join(" -o ", tokens.Select(token => $"-iname {Q($"*{token}*")}"));
        var command =
            $"find {Q(location.Directory)} -type f \\( {patterns} \\) 2>/dev/null | head -n 8";
        await _searchGate.WaitAsync();
        CommandResult result;
        try
        {
            result = location.RequiresRoot
                ? await _adb.RootShellAsync(command)
                : await _adb.ShellAsync(command);
        }
        finally
        {
            _searchGate.Release();
        }
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

        var locations = roots
            .SelectMany(root => FoldersFor(kind).Select(folder =>
                new MediaSearchLocation($"{root.Path}/{folder}", folder, root.RequiresRoot)))
            .ToList();
        if (kind == MessageKind.File)
        {
            locations.Add(new MediaSearchLocation(
                "/sdcard/Android/data/com.tencent.mm/MicroMsg/Download", "Download", false));
            locations.Add(new MediaSearchLocation(
                "/sdcard/tencent/MicroMsg/Download", "Download", false));
            locations.Add(new MediaSearchLocation(
                "/sdcard/Download/WeiXin", "Download", false));
        }
        return locations.DistinctBy(x => x.Directory, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<string> GetSearchTokens(WeChatMessage message)
    {
        var tokens = new List<string>();

        if (message.Kind == MessageKind.File)
        {
            if (!AddMd5Token(tokens, ReadXmlValue(message.Content, "md5")) &&
                !AddGeneralToken(tokens, message.ImgPath ?? ""))
                AddGeneralToken(tokens, ReadXmlValue(message.Content, "title"));
        }
        else
        {
            AddGeneralToken(tokens, message.ImgPath ?? "");
            if (message.Kind == MessageKind.Emoji)
            {
                AddMd5Token(tokens, ReadXmlValue(message.Content, "md5"));
                AddMd5Token(tokens, ReadXmlValue(message.Content, "newmd5"));
            }
        }

        return tokens
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool AddMd5Token(List<string> tokens, string value)
    {
        var token = SanitizeToken(value);
        if (token.Length != 32 || !token.All(Uri.IsHexDigit)) return false;
        tokens.Add(token);
        return true;
    }

    private static bool AddGeneralToken(List<string> tokens, string value)
    {
        var token = SanitizeToken(value);
        if (token.Length < 3) return false;
        tokens.Add(token);
        return true;
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
        MessageKind.File => ["attachment", "Download", "download"],
        _ => ["image2", "video", "voice2", "emoji"]
    };

    private static string SanitizeToken(string input)
    {
        var value = input.Replace("THUMBNAIL_DIRPATH://", "", StringComparison.OrdinalIgnoreCase);
        value = value.Replace("th_", "", StringComparison.OrdinalIgnoreCase);
        value = Path.GetFileNameWithoutExtension(value.Trim());
        return new string(value.Where(c =>
            char.IsLetterOrDigit(c) || c is '_' or '-' or ' ' or '&').ToArray()).Trim();
    }

    private static string ReadXmlValue(string content, string name)
    {
        if (string.IsNullOrWhiteSpace(content)) return "";
        var tag = Regex.Match(content,
            $"<{Regex.Escape(name)}>(.*?)</{Regex.Escape(name)}>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var value = tag.Success ? tag.Groups[1].Value.Trim() : "";
        if (value.Length == 0)
        {
            var attribute = Regex.Match(content,
                $"\\b{Regex.Escape(name)}\\s*=\\s*['\"]([^'\"]*)['\"]",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            value = attribute.Success ? attribute.Groups[1].Value.Trim() : "";
        }
        if (value.StartsWith("<![CDATA[", StringComparison.OrdinalIgnoreCase) &&
            value.EndsWith("]]>", StringComparison.Ordinal))
            value = value[9..^3];
        return WebUtility.HtmlDecode(value).Trim();
    }

    private sealed record MediaCacheKey(
        string Serial,
        string AccountDirectory,
        string ExternalAccountDirectory,
        MessageKind Kind,
        long MessageId,
        string SearchTokenSignature);
}

public sealed record MediaSearchLocation(
    string Directory,
    string Role,
    bool RequiresRoot);
