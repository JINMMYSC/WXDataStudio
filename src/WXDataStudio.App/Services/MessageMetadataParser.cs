using System.Net;
using System.Text.RegularExpressions;
using WXDataStudio.App.Models;

namespace WXDataStudio.App.Services;

public static partial class MessageMetadataParser
{
    public static MessageMetadata Parse(MessageKind kind, string? content)
    {
        var text = content ?? "";
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
            TransactionType = MessageKindPolicy.IsSensitive(kind) ? kind.ToString() : "",
            TransactionAmount = First(text, "feedesc", "fee", "amount"),
            TransactionStatus = First(text, "pay_memo", "receiver_name", "paysubtype"),
            TransactionMemo = First(text, "pay_memo", "remark", "payinfo")
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
