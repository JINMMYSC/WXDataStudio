using WXDataStudio.App.Models;

namespace WXDataStudio.App.Services;

public static class MessageTypeClassifier
{
    public static MessageKind Classify(int type, string? content)
    {
        var text = content ?? "";
        var normalizedType = type & 0xffff;
        if (normalizedType == 0) normalizedType = type;
        return normalizedType switch
        {
            1 => MessageKind.Text,
            3 => MessageKind.Image,
            34 => MessageKind.Voice,
            43 or 62 => MessageKind.Video,
            47 => MessageKind.Emoji,
            48 => MessageKind.Location,
            50 => MessageKind.Call,
            10000 or 10002 => MessageKind.System,
            49 => ClassifyAppMessage(text),
            _ => ClassifyByContent(normalizedType, text)
        };
    }

    private static MessageKind ClassifyAppMessage(string content)
    {
        var subtype = ReadIntTag(content, "type");
        return subtype switch
        {
            5 or 51 or 62 => MessageKind.Link,
            6 => MessageKind.File,
            19 or 57 => MessageKind.Quote,
            33 or 36 => MessageKind.MiniProgram,
            2000 => MessageKind.Transfer,
            2001 => MessageKind.RedPacket,
            _ => ClassifyByContent(49, content)
        };
    }

    private static MessageKind ClassifyByContent(int type, string content)
    {
        if (ContainsAny(content, "wxpay://", "<pay_info>", "<wcpayinfo>"))
        {
            if (ContainsAny(content, "hongbao", "红包", "sendid")) return MessageKind.RedPacket;
            if (ContainsAny(content, "transfer", "转账", "paysubtype")) return MessageKind.Transfer;
            return MessageKind.Payment;
        }
        if (ContainsAny(content, "<msg location", "label=", "poiname=")) return MessageKind.Location;
        if (ContainsAny(content, "<appattach>", "<totallen>", "<fileext>")) return MessageKind.File;
        if (ContainsAny(content, "<weappinfo>", "<appservicetype>")) return MessageKind.MiniProgram;
        if (ContainsAny(content, "<refermsg>", "<refermsgid>")) return MessageKind.Quote;
        if (type == 42 || ContainsAny(content, "<username>", "<nickname>")) return MessageKind.ContactCard;
        return MessageKind.Unknown;
    }

    private static bool ContainsAny(string text, params string[] needles) =>
        needles.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));

    private static int ReadIntTag(string xml, string tag)
    {
        var open = "<" + tag + ">";
        var close = "</" + tag + ">";
        var start = xml.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return 0;
        start += open.Length;
        var end = xml.IndexOf(close, start, StringComparison.OrdinalIgnoreCase);
        if (end <= start) return 0;
        return int.TryParse(xml[start..end].Trim(), out var value) ? value : 0;
    }

    /// <summary>
    /// Returns the numeric appmsg sub-type for a type-49 message, or 0 when the
    /// payload carries no explicit sub-type.
    /// </summary>
    public static int ReadAppMessageSubtype(string? content) => ReadIntTag(content ?? "", "type");
}
