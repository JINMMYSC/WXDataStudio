namespace WXDataStudio.App.Models;

public sealed class WeChatMessage
{
    public long LocalId { get; init; }
    public long? ServerId { get; init; }
    public string ConversationId { get; init; } = "";
    public string Sender { get; init; } = "";
    public bool IsOutgoing { get; init; }
    public int RawType { get; init; }
    public int RawStatus { get; init; }
    public long CreateTime { get; init; }
    public long Sequence { get; init; }
    public string Content { get; init; } = "";
    public string? ImgPath { get; init; }
    public string? Reserved { get; init; }
    public string? LvBufferHex { get; init; }
    public MessageKind Kind { get; init; }
    public string DisplayTime => DateTimeOffset.FromUnixTimeSeconds(CreateTime).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
    public bool Sensitive => MessageKindPolicy.IsSensitive(Kind);
    public string Direction => IsOutgoing ? "我发送" : "对方发送";
    public string StatusText => RawStatus.ToString();
    public override string ToString() => $"[{DisplayTime}] {Direction} · {Kind} · {Preview(Content)}";
    private static string Preview(string s) => s.Length <= 70 ? s : s[..70] + "…";
}
