using System.Net;
using System.Text.RegularExpressions;
using WXDataStudio.App.Models;

namespace WXDataStudio.App.Services;

public static class MessageMetadataParser
{
    public static MessageMetadata Parse(MessageKind kind, string? content)
    {
        var text = content ?? "";
        var refer = Section(text, "refermsg");
        var weapp = Section(text, "weappinfo");
        return new MessageMetadata
        {
            Title = Tag(text, "title"),
            Description = Tag(text, "des"),
            Url = Tag(text, "url"),
            AppId = First(text, "appid", "appId", "weappappid"),
            Address = First(text, "label", "poiname", "address"),
            Latitude = First(text, "x", "latitude"),
            Longitude = First(text, "y", "longitude"),
            FileExtension = Tag(text, "fileext"),
            FileSize = First(text, "totallen", "filesize"),
            QuoteSender = First(refer, "displayname", "fromusr", "chatusr"),
            QuoteContent = First(refer, "content", "refercontent", "msgsource"),
            MiniProgramUserName = First(weapp, "username", "weappusername"),
            MiniProgramPath = First(weapp, "pagepath", "path"),
            ContactUserName = kind == MessageKind.ContactCard ? First(text, "username", "encryptusername") : "",
            ContactNickName = kind == MessageKind.ContactCard ? First(text, "nickname", "fullpy") : "",
            TransactionType = MessageKindPolicy.IsSensitive(kind) ? kind.ToString() : "",
            TransactionAmount = First(text, "feedesc", "fee", "amount"),
            TransactionStatus = First(text, "pay_memo", "receiver_name", "paysubtype", "state"),
            TransactionMemo = First(text, "pay_memo", "remark", "payinfo", "desc")
        };
    }

    private static string First(string text, params string[] tags)
    {
        foreach (var tag in tags)
        {
            var value = Tag(text, tag);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return "";
    }

    private static string Section(string text, string name)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var escaped = Regex.Escape(name);
        var match = Regex.Match(text, $@"<{escaped}[^>]*>(?<v>.*?)</{escaped}>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? match.Groups["v"].Value : "";
    }

    private static string Tag(string text, string name)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var escaped = Regex.Escape(name);
        var tagMatch = Regex.Match(text,
            $@"<{escaped}>(?:<!\[CDATA\[)?(?<v>.*?)(?:\]\]>)?</{escaped}>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (tagMatch.Success) return Clean(tagMatch.Groups["v"].Value);

        var attrMatch = Regex.Match(text,
            $@"\b{escaped}\s*=\s*[""'](?<v>.*?)[""']",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return attrMatch.Success ? Clean(attrMatch.Groups["v"].Value) : "";
    }

    private static string Clean(string value)
    {
        var text = value.Trim();
        if (text.StartsWith("<![CDATA[", StringComparison.Ordinal)) text = text[9..];
        if (text.EndsWith("]]>", StringComparison.Ordinal)) text = text[..^3];
        return WebUtility.HtmlDecode(text.Trim());
    }
}
