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
        return "<msg><appmsg>" +
               $"<type>{appType}</type>" +
               $"<title>{title}</title>" +
               $"<des>{title}</des>" +
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
        var status = Tag(text, "state");
        if (status.Length == 0) status = Tag(text, "receiver_name");
        return (amount, note, status);
    }

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

    private static string E(string value) => WebUtility.HtmlEncode(value ?? "");
}
