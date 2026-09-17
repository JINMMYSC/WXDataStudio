using System.Security.Cryptography;
using System.Text;
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

    public async Task<IReadOnlyList<DatabaseKeyCandidate>> BuildAsync()
    {
        var uin = await ReadUinAsync();
        if (string.IsNullOrWhiteSpace(uin)) return Array.Empty<DatabaseKeyCandidate>();

        var tokens = await ReadDeviceTokensAsync();
        var candidates = new List<DatabaseKeyCandidate>();
        foreach (var uinVariant in ExpandUinVariants(uin))
        foreach (var item in tokens)
        {
            var deviceFirst = BuildLegacyKey(item.Value, uinVariant);
            if (!string.IsNullOrWhiteSpace(deviceFirst))
                candidates.Add(new DatabaseKeyCandidate(deviceFirst, item.Source + ":device+uin"));

            var uinFirst = BuildLegacyKey(uinVariant, item.Value);
            if (!string.IsNullOrWhiteSpace(uinFirst))
                candidates.Add(new DatabaseKeyCandidate(uinFirst, item.Source + ":uin+device"));
        }

        return candidates.DistinctBy(x => x.Password).ToArray();
    }

    public static string BuildLegacyKey(string deviceToken, string uin)
    {
        if (string.IsNullOrWhiteSpace(deviceToken) || string.IsNullOrWhiteSpace(uin)) return "";
        var bytes = Encoding.UTF8.GetBytes(deviceToken.Trim() + uin.Trim());
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
    private async Task<string> ReadUinAsync()
    {
        var paths = new[]
        {
            "/data/user/0/com.tencent.mm/shared_prefs/auth_info_key_prefs.xml",
            "/data/data/com.tencent.mm/shared_prefs/auth_info_key_prefs.xml"
        };
        foreach (var path in paths)
        {
            var result = await _adb.RootShellAsync($"cat '{path}' 2>/dev/null");
            if (!result.Success && string.IsNullOrWhiteSpace(result.StdOut)) continue;
            var uin = ParseUin(result.StdOut);
            if (!string.IsNullOrWhiteSpace(uin)) return uin;
        }
        return "";
    }

    internal static string ParseUin(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return "";
        var patterns = new[]
        {
            @"name=[""']_auth_uin[""'][^>]*value=[""'](?<v>-?\d+)[""']",
            @"name=[""']_auth_uin[""'][^>]*>(?<v>-?\d+)<"
        };
        foreach (var pattern in patterns)
        {
            var match = Regex.Match(xml, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (match.Success) return match.Groups["v"].Value;
        }
        return "";
    }

    private async Task<IReadOnlyList<(string Source, string Value)>> ReadDeviceTokensAsync()
    {
        var probes = new[]
        {
            ("persist.radio.imei", "getprop persist.radio.imei"),
            ("ro.ril.oem.imei", "getprop ro.ril.oem.imei"),
            ("ril.gsm.imei", "getprop ril.gsm.imei"),
            ("android_id", "settings get secure android_id"),
            ("ro.serialno", "getprop ro.serialno")
        };
        var list = new List<(string Source, string Value)>();
        foreach (var probe in probes)
        {
            var result = await _adb.ShellAsync(probe.Item2);
            var raw = result.StdOut.Trim();
            if (string.IsNullOrWhiteSpace(raw) || raw.Equals("null", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var token in raw.Split(new[] { ',', '\r', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var cleaned = new string(token.Where(char.IsLetterOrDigit).ToArray());
                if (cleaned.Length >= 6) list.Add((probe.Item1, cleaned));
            }
        }
        return list;
    }

    public async Task<LegacyKeyDiagnostics> DiagnoseAsync()
    {
        var uin = await ReadUinAsync();
        var tokens = await ReadDeviceTokensAsync();
        var count = 0;
        if (!string.IsNullOrWhiteSpace(uin))
            count = tokens.Select(x => BuildLegacyKey(x.Value, uin)).Where(x => x.Length > 0).Distinct().Count();
        return new LegacyKeyDiagnostics(!string.IsNullOrWhiteSpace(uin), tokens.Select(x => x.Source).Distinct().ToArray(), count);
    }
}

public sealed record LegacyKeyDiagnostics(
    bool UinFound,
    IReadOnlyList<string> TokenSources,
    int CandidateCount);
