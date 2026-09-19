using System.Net;
using WXDataStudio.App.Models;

namespace WXDataStudio.App.Services;

/// <summary>
/// Builds and reads the payload of transfer, red-packet and payment records so
/// the editor can ask for an amount and a note instead of raw markup.
/// </summary>
public static class TransactionMessageTemplate
{
    public static string Build(MessageKind kind, string amount, string note, string status)
    {
        var appType = kind switch
        {
            MessageKind.RedPacket => 2001,
            _ => 2000
        };
        var paySubType = kind switch
        {
            MessageKind.RedPacket => 1,
            MessageKind.Payment => 3,
            _ => 1
        };
        var title = E(string.IsNullOrWhiteSpace(note) ? DefaultTitle(kind) : note);
        // WeChat renders the bubble's status line from <des>.
        var statusText = E(string.IsNullOrWhiteSpace(status) ? title : status);
        return "<msg><appmsg>" +
               $"<type>{appType}</type>" +
               $"<title>{title}</title>" +
               $"<des>{statusText}</des>" +
               "<wcpayinfo>" +
               $"<feedesc>{E(string.IsNullOrWhiteSpace(amount) ? "¥0.00" : amount)}</feedesc>" +
               $"<pay_memo>{E(note)}</pay_memo>" +
               $"<paysubtype>{paySubType}</paysubtype>" +
               $"<state>{E(status)}</state>" +
               "</wcpayinfo></appmsg></msg>";
    }

    public static (string Amount, string Note, string Status) Read(MessageKind kind, string? content)
    {
        var text = content ?? "";
        var amount = Tag(text, "feedesc");
        var note = Tag(text, "pay_memo");
        if (note.Length == 0) note = Tag(text, "title");
        // The bubble shows the status from des/senderdes/receiverdes.
        var status = Tag(text, "des");
        if (status.Length == 0) status = Tag(text, "senderdes");
        if (status.Length == 0) status = Tag(text, "receiverdes");
        if (status.Length == 0) status = Tag(text, "state");
        if (status.Length == 0) status = Tag(text, "receiver_name");
        return (amount, note, status);
    }

    /// <summary>
    /// Edits an existing transfer / red-packet record in place: only the amount,
    /// the note and the status text change, so transaction ids, usernames and
    /// every other field WeChat needs stay exactly as they were. Rebuilding the
    /// payload from scratch stripped those ids and made WeChat fail to open the
    /// transfer detail.
    /// </summary>
    public static string Update(
        string? originalContent, MessageKind kind, string amount, string note, string status)
    {
        var text = originalContent ?? "";
        if (text.Length == 0 || !text.Contains("<wcpayinfo", StringComparison.OrdinalIgnoreCase))
            return Build(kind, amount, note, status);

        var updated = text;
        if (!string.IsNullOrWhiteSpace(amount))
        {
            updated = HasTag(updated, "feedesc")
                ? SetTag(updated, "feedesc", amount)
                : InsertInside(updated, "wcpayinfo", "feedesc", amount);
        }
        if (!string.IsNullOrWhiteSpace(note)) updated = SetTag(updated, "pay_memo", note);
        if (!string.IsNullOrWhiteSpace(status))
        {
            var replaced = false;
            foreach (var tag in new[] { "des", "senderdes", "receiverdes", "state" })
            {
                if (!HasTag(updated, tag)) continue;
                updated = SetTag(updated, tag, status);
                replaced = true;
            }
            if (!replaced)
                updated = InsertInside(updated, "wcpayinfo", "des", status);
        }
        return updated;
    }

    private static bool HasTag(string xml, string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            xml, $"<{name}[ >]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string SetTag(string xml, string name, string value) =>
        System.Text.RegularExpressions.Regex.Replace(
            xml,
            $"(<{name}[^>]*>)(.*?)(</{name}>)",
            match => match.Groups[1].Value + E(value) + match.Groups[3].Value,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Singleline);

    private static string InsertInside(string xml, string parent, string name, string value) =>
        System.Text.RegularExpressions.Regex.Replace(
            xml,
            $"(<{parent}[^>]*>)(.*?)(</{parent}>)",
            match => match.Groups[1].Value + $"<{name}>{E(value)}</{name}>" +
                     match.Groups[2].Value + match.Groups[3].Value,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Singleline);

    private static string Tag(string text, string name)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var match = System.Text.RegularExpressions.Regex.Match(
            text,
            $"<{name}>(?:<!\\[CDATA\\[)?(?<v>.*?)(?:\\]\\]>)?</{name}>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Singleline);
        return match.Success ? WebUtility.HtmlDecode(match.Groups["v"].Value.Trim()) : "";
    }

    private static string DefaultTitle(MessageKind kind) => kind switch
    {
        MessageKind.RedPacket => "微信红包",
        MessageKind.Payment => "收付款",
        _ => "转账"
    };

    /// <summary>
    /// XML-escapes only the characters that must be escaped, so a currency sign
    /// such as ¥ stays readable in the message WeChat stores and renders.
    /// </summary>
    private static string E(string value) => (value ?? "")
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&apos;", StringComparison.Ordinal);
}
