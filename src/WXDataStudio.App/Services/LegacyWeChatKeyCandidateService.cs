using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WXDataStudio.App.Services;

public sealed record DatabaseKeyCandidate(string Password, string Source);

public sealed class LegacyWeChatKeyCandidateService
{
    private readonly AdbService _adb;

    public LegacyWeChatKeyCandidateService(AdbService adb)
    {
        _adb = adb;
    }

    public async Task<IReadOnlyList<DatabaseKeyCandidate>> BuildAsync(string? snapshotDirectory = null)
    {
        var uins = await ReadUinsAsync(snapshotDirectory);
        if (uins.Count == 0) return Array.Empty<DatabaseKeyCandidate>();

        var tokens = await ReadDeviceTokensAsync(snapshotDirectory);
        var candidates = new List<DatabaseKeyCandidate>();
        foreach (var uin in uins)
        foreach (var uinVariant in ExpandUinVariants(uin))
        {
            var emptyDevice = BuildLegacyKey("", uinVariant);
            if (!string.IsNullOrWhiteSpace(emptyDevice))
                candidates.Add(new DatabaseKeyCandidate(emptyDevice, "empty-device+uin"));

            foreach (var item in tokens)
            {
                var deviceFirst = BuildLegacyKey(item.Value, uinVariant);
                if (!string.IsNullOrWhiteSpace(deviceFirst))
                    candidates.Add(new DatabaseKeyCandidate(deviceFirst, item.Source + ":device+uin"));

                var uinFirst = BuildLegacyKey(uinVariant, item.Value);
                if (!string.IsNullOrWhiteSpace(uinFirst))
                    candidates.Add(new DatabaseKeyCandidate(uinFirst, item.Source + ":uin+device"));
            }
        }

        return candidates.DistinctBy(x => x.Password).Take(256).ToArray();
    }

    public static string BuildLegacyKey(string deviceToken, string uin)
    {
        if (string.IsNullOrWhiteSpace(uin)) return "";
        var bytes = Encoding.UTF8.GetBytes((deviceToken ?? "").Trim() + uin.Trim());
        var md5 = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
        return md5[..7];
    }

    internal static IReadOnlyList<string> ExpandUinVariants(string uin)
    {
        var list = new List<string> { uin.Trim() };
        if (int.TryParse(uin, out var signed) && signed < 0)
            list.Add(unchecked((uint)signed).ToString());
        return list.Distinct().ToArray();
    }

    private async Task<IReadOnlyList<string>> ReadUinsAsync(string? snapshotDirectory)
    {
        var values = new List<string>();
        if (!string.IsNullOrWhiteSpace(snapshotDirectory))
        {
            var support = Path.Combine(snapshotDirectory, "support");
            foreach (var name in new[]
                     {
                         "auth_info_key_prefs.xml",
                         "system_config_prefs.xml",
                         "com.tencent.mm_preferences.xml"
                     })
            {
                var path = Path.Combine(support, name);
                if (!File.Exists(path)) continue;
                try { values.AddRange(ParseUins(await File.ReadAllTextAsync(path))); }
                catch { }
            }
        }

        if (values.Count > 0) return values.Distinct().Take(16).ToArray();

        var paths = new[]
        {
            "/data/user/0/com.tencent.mm/shared_prefs/auth_info_key_prefs.xml",
            "/data/user/0/com.tencent.mm/shared_prefs/system_config_prefs.xml",
            "/data/user/0/com.tencent.mm/shared_prefs/com.tencent.mm_preferences.xml",
            "/data/data/com.tencent.mm/shared_prefs/auth_info_key_prefs.xml",
            "/data/data/com.tencent.mm/shared_prefs/system_config_prefs.xml",
            "/data/data/com.tencent.mm/shared_prefs/com.tencent.mm_preferences.xml"
        };
        foreach (var path in paths)
        {
            try
            {
                var result = await _adb.RootShellAsync($"cat '{path}' 2>/dev/null");
                if (!result.Success && string.IsNullOrWhiteSpace(result.StdOut)) continue;
                values.AddRange(ParseUins(result.StdOut));
            }
            catch { }
        }
        return values.Distinct().Take(16).ToArray();
    }

    internal static string ParseUin(string xml) => ParseUins(xml).FirstOrDefault() ?? "";

    public static IReadOnlyList<string> ParseUins(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return Array.Empty<string>();
        var patterns = new[]
        {
            @"name=[""']_auth_uin[""'][^>]*value=[""'](?<v>-?\d+)[""']",
            @"name=[""']_auth_uin[""'][^>]*>(?<v>-?\d+)<",
            @"name=[""']default_uin[""'][^>]*value=[""'](?<v>-?\d+)[""']",
            @"name=[""']default_uin[""'][^>]*>(?<v>-?\d+)<",
            @"name=[""']last_login_uin[""'][^>]*value=[""'](?<v>-?\d+)[""']",
            @"name=[""']last_login_uin[""'][^>]*>(?<v>-?\d+)<"
        };
        var values = new List<string>();
        foreach (var pattern in patterns)
        foreach (Match match in Regex.Matches(xml, pattern,
                     RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var value = match.Groups["v"].Value;
            if (!string.IsNullOrWhiteSpace(value) && value != "0") values.Add(value);
        }
        return values.Distinct().ToArray();
    }

    private async Task<IReadOnlyList<(string Source, string Value)>> ReadDeviceTokensAsync(
        string? snapshotDirectory)
    {
        var list = new List<(string Source, string Value)>();
        if (!string.IsNullOrWhiteSpace(snapshotDirectory))
        {
            var support = Path.Combine(snapshotDirectory, "support");
            var tokenPath = Path.Combine(support, "device_tokens.json");
            if (File.Exists(tokenPath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(tokenPath);
                    var tokens = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (tokens is not null)
                    {
                        foreach (var item in tokens)
                            AddRawTokens(list, "snapshot:" + item.Key, item.Value);
                    }
                }
                catch { }
            }

            var compatible = Path.Combine(support, "CompatibleInfo.cfg");
            if (File.Exists(compatible))
            {
                try
                {
                    var printable = ExtractPrintableAscii(await File.ReadAllBytesAsync(compatible));
                    foreach (var value in ExtractCompatibleInfoCandidates(printable))
                        list.Add(("snapshot:CompatibleInfo.cfg", value));
                }
                catch { }
            }
        }

        if (list.Count > 0)
            return NormalizeTokens(list);

        var probes = new[]
        {
            ("persist.radio.imei", "getprop persist.radio.imei"),
            ("ro.ril.oem.imei", "getprop ro.ril.oem.imei"),
            ("ril.gsm.imei", "getprop ril.gsm.imei"),
            ("android_id", "settings get secure android_id"),
            ("ro.serialno", "getprop ro.serialno")
        };
        foreach (var probe in probes)
        {
            try
            {
                var result = await _adb.ShellAsync(probe.Item2);
                AddRawTokens(list, probe.Item1, result.StdOut);
            }
            catch { }
        }

        foreach (var item in await ReadCompatibleInfoTokensFromDeviceAsync())
            list.Add(item);

        return NormalizeTokens(list);
    }

    private static void AddRawTokens(
        List<(string Source, string Value)> list, string source, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) ||
            raw.Trim().Equals("null", StringComparison.OrdinalIgnoreCase)) return;
        foreach (var token in raw.Split(new[] { ',', '\r', '\n', ' ' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var cleaned = new string(token.Where(char.IsLetterOrDigit).ToArray());
            if (cleaned.Length >= 6) list.Add((source, cleaned));
        }
    }

    private static IReadOnlyList<(string Source, string Value)> NormalizeTokens(
        IEnumerable<(string Source, string Value)> source) =>
        source.Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .DistinctBy(x => x.Value, StringComparer.OrdinalIgnoreCase)
            .Take(64)
            .ToArray();

    private async Task<IReadOnlyList<(string Source, string Value)>> ReadCompatibleInfoTokensFromDeviceAsync()
    {
        var paths = new[]
        {
            "/data/user/0/com.tencent.mm/MicroMsg/CompatibleInfo.cfg",
            "/data/data/com.tencent.mm/MicroMsg/CompatibleInfo.cfg"
        };
        foreach (var path in paths)
        {
            try
            {
                var command =
                    $"if [ -f '{path}' ]; then " +
                    $"if command -v busybox >/dev/null 2>&1; then busybox strings '{path}'; " +
                    $"elif command -v strings >/dev/null 2>&1; then strings '{path}'; " +
                    $"else cat '{path}' | tr -cd '\\11\\12\\15\\40-\\176'; fi; fi";
                var result = await _adb.RootShellAsync(command);
                if (string.IsNullOrWhiteSpace(result.StdOut)) continue;
                var values = ExtractCompatibleInfoCandidates(result.StdOut);
                if (values.Count > 0)
                    return values.Select(x => ("CompatibleInfo.cfg", x)).ToArray();
            }
            catch { }
        }
        return Array.Empty<(string Source, string Value)>();
    }

    private static string ExtractPrintableAscii(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        foreach (var value in bytes)
        {
            if (value is >= 32 and <= 126 || value is 9 or 10 or 13)
                sb.Append((char)value);
            else
                sb.Append('\n');
        }
        return sb.ToString();
    }

    public static IReadOnlyList<string> ExtractCompatibleInfoCandidates(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        var values = new List<string>();

        foreach (Match match in Regex.Matches(text, @"(?<!\d)\d{14,18}(?!\d)"))
            values.Add(match.Value);

        foreach (Match match in Regex.Matches(text,
                     @"(?<![A-Za-z0-9])[A-Fa-f0-9]{14,20}(?![A-Za-z0-9])"))
            values.Add(match.Value);

        return values
            .Where(x => x.Length >= 14)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToArray();
    }

    public async Task<LegacyKeyDiagnostics> DiagnoseAsync(string? snapshotDirectory = null)
    {
        var uins = await ReadUinsAsync(snapshotDirectory);
        var tokens = await ReadDeviceTokensAsync(snapshotDirectory);
        var count = 0;
        if (uins.Count > 0)
        {
            count = uins
                .SelectMany(ExpandUinVariants)
                .SelectMany(uin => tokens.Select(x => BuildLegacyKey(x.Value, uin))
                    .Append(BuildLegacyKey("", uin)))
                .Where(x => x.Length > 0)
                .Distinct()
                .Count();
        }
        return new LegacyKeyDiagnostics(
            uins.Count > 0,
            tokens.Select(x => x.Source).Distinct().ToArray(),
            count);
    }
}

public sealed record LegacyKeyDiagnostics(
    bool UinFound,
    IReadOnlyList<string> TokenSources,
    int CandidateCount);
