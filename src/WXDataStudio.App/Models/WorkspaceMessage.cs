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
    /// <summary>Transaction-class record: labelled in the UI, still editable.</summary>
    public bool IsTransaction { get; init; }
    public bool IsNew { get; init; }
    public string OriginalContent { get; init; } = "";
    public long OriginalCreateTime { get; init; }
    public string? OriginalAttachment { get; init; }
    public string Content { get; set; } = "";
    public long CreateTime { get; set; }
    public string? Attachment { get; set; }

    public bool IsDirty => IsNew || Content != OriginalContent || CreateTime != OriginalCreateTime ||
                           !string.Equals(Attachment, OriginalAttachment, StringComparison.Ordinal);
    public bool CanEdit => MessageKindPolicy.IsEditable(Kind);
    public string DisplayTime => SafeFormatTime(CreateTime);
    public string Direction => IsOutgoing ? "我发送" : "对方发送";
    public override string ToString() =>
        $"{(IsNew ? "[新增] " : "")}[{DisplayTime}] {Direction} · {Kind} · {Preview(Content)}";
    private static string Preview(string s) => s.Length <= 70 ? s : s[..70] + "…";
    private static string SafeFormatTime(long unix)
    {
        if (unix <= 0) return "";
        try { return DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"); }
        catch (ArgumentOutOfRangeException) { return ""; }
    }

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
        IsTransaction = source.IsTransaction,
        OriginalContent = source.Content,
        OriginalCreateTime = source.CreateTime,
        OriginalAttachment = source.ImgPath,
        Content = source.Content,
        CreateTime = source.CreateTime,
        Attachment = source.ImgPath
    };

    public static WorkspaceMessage CreateNew(
        long localId, string conversationId, MessageKind kind, long createTime, string content, string? attachment) => new()
    {
        LocalId = localId,
        ConversationId = conversationId,
        IsOutgoing = true,
        Kind = kind,
        IsTransaction = MessageKindPolicy.IsTransaction(kind),
        IsNew = true,
        OriginalContent = "",
        OriginalCreateTime = 0,
        Content = content,
        CreateTime = createTime,
        Attachment = attachment
    };
}
