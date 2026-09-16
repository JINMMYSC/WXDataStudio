namespace WXDataStudio.App.Models;

public sealed class WorkspaceMessage
{
    public long LocalId { get; init; }
    public long? ServerId { get; init; }
    public string ConversationId { get; init; } = "";
    public string Sender { get; init; } = "";
    public bool IsOutgoing { get; init; }
    public int RawType { get; init; }
    public int RawStatus { get; init; }
    public long Sequence { get; init; }
    public MessageKind Kind { get; init; }
    public bool Sensitive { get; init; }
    public string OriginalContent { get; init; } = "";
    public long OriginalCreateTime { get; init; }
    public string? OriginalAttachment { get; init; }
    public string Content { get; set; } = "";
    public long CreateTime { get; set; }
    public string? Attachment { get; set; }
    public bool IsDirty => Content != OriginalContent || CreateTime != OriginalCreateTime ||
                           !string.Equals(Attachment, OriginalAttachment, StringComparison.Ordinal);
    public bool CanEdit => !Sensitive;
    public string DisplayTime => DateTimeOffset.FromUnixTimeSeconds(CreateTime)
        .LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
    public string Direction => IsOutgoing ? "我发送" : "对方发送";
    public override string ToString() => $"[{DisplayTime}] {Direction} · {Kind} · {Preview(Content)}";
    private static string Preview(string s) => s.Length <= 70 ? s : s[..70] + "…";

    public static WorkspaceMessage From(WeChatMessage source) => new()
    {
        LocalId = source.LocalId,
        ServerId = source.ServerId,
        ConversationId = source.ConversationId,
        Sender = source.Sender,
        IsOutgoing = source.IsOutgoing,
        RawType = source.RawType,
        RawStatus = source.RawStatus,
        Sequence = source.Sequence,
        Kind = source.Kind,
        Sensitive = source.Sensitive,
        OriginalContent = source.Content,
        OriginalCreateTime = source.CreateTime,
        OriginalAttachment = source.ImgPath,
        Content = source.Content,
        CreateTime = source.CreateTime,
        Attachment = source.ImgPath
    };
}
