using System.IO;
using WXDataStudio.App.Models;

namespace WXDataStudio.App.Services;

/// <summary>
/// Pulls a picture that only exists on the phone into a local cache so the chat
/// view can show it. Files are cached by their search token, so looking at the
/// same message twice does not transfer it again.
/// </summary>
public sealed class MediaCacheService
{
    private readonly AdbService _adb;
    private readonly MediaLocatorService _locator;
    private readonly string _root;
    private readonly Dictionary<string, string?> _results = new(StringComparer.OrdinalIgnoreCase);

    public MediaCacheService(AdbService adb, MediaLocatorService locator, string? root = null)
    {
        _adb = adb;
        _locator = locator;
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "WXDataStudio", "media-cache");
    }

    public async Task<string?> FetchAsync(
        DeviceInfo device, WeChatMessage message, CancellationToken cancellationToken = default)
    {
        if (message.Kind is not (MessageKind.Image or MessageKind.Emoji)) return null;

        var tokens = MediaLocatorService.GetSearchTokens(message);
        if (tokens.Count == 0) return null;
        var key = tokens[0];
        if (_results.TryGetValue(key, out var cached)) return cached;

        try
        {
            var resolution = await _locator.ResolveAsync(device, message);
            var candidate = resolution.Candidates.FirstOrDefault();
            if (candidate is null || string.IsNullOrWhiteSpace(candidate.RemotePath))
            {
                _results[key] = null;
                return null;
            }

            Directory.CreateDirectory(_root);
            var extension = Path.GetExtension(candidate.RemotePath);
            if (extension.Length == 0 || extension.Length > 8) extension = ".jpg";
            var localPath = Path.Combine(_root, Safe(key) + extension);
            if (File.Exists(localPath) && new FileInfo(localPath).Length > 0)
            {
                _results[key] = localPath;
                return localPath;
            }

            var pulled = await _adb.RootPullFileAsync(candidate.RemotePath, localPath);
            if (!pulled.Success || !File.Exists(localPath) || new FileInfo(localPath).Length == 0)
            {
                _results[key] = null;
                return null;
            }

            _results[key] = localPath;
            return localPath;
        }
        catch
        {
            _results[key] = null;
            return null;
        }
    }

    private static string Safe(string value)
    {
        var chars = value.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-').ToArray();
        var text = new string(chars);
        return text.Length == 0 ? "media" : text.Length <= 64 ? text : text[..64];
    }
}
