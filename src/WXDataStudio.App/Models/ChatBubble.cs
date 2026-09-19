using System.IO;
using WXDataStudio.App.Services;

namespace WXDataStudio.App.Models;

/// <summary>
/// One rendered chat row. It carries the original message object in
/// <see cref="Source"/> so selection and editing keep working, plus the fields
/// the WeChat-style bubble template needs.
/// </summary>
public sealed class ChatBubble
{
    public object Source { get; init; } = default!;
    public long LocalId { get; init; }
    public bool IsOutgoing { get; init; }
    public string DayLabel { get; init; } = "";
    public bool ShowDay { get; init; }
    public string Sender { get; init; } = "";
    public bool ShowSender { get; init; }
    public string Time { get; init; } = "";
    public string KindLabel { get; init; } = "";
    public string Body { get; init; } = "";
    public bool IsTransaction { get; init; }
    public bool IsNew { get; init; }
    public bool IsDirty { get; init; }
    public string? LocalImagePath { get; init; }
    public string AttachmentNote { get; init; } = "";
    public bool HasLocalImage => !string.IsNullOrWhiteSpace(LocalImagePath);
    public bool HasAttachmentNote => !string.IsNullOrWhiteSpace(AttachmentNote);
    public bool HasKindLabel => !string.IsNullOrWhiteSpace(KindLabel);

    public static ChatBubble FromMessage(WeChatMessage message, bool showDay, string dayLabel) => new()
    {
        Source = message,
        LocalId = message.LocalId,
        IsOutgoing = message.IsOutgoing,
        DayLabel = dayLabel,
        ShowDay = showDay,
        Sender = message.Sender,
        ShowSender = !message.IsOutgoing &&
                     message.ConversationId.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrWhiteSpace(message.Sender),
        Time = ShortTime(message.CreateTime),
        KindLabel = KindLabelFor(message.Kind),
        Body = ChatExportService.Describe(message),
        IsTransaction = message.IsTransaction,
        LocalImagePath = LocalImage(message.Kind, message.ImgPath),
        AttachmentNote = AttachmentNoteFor(message.Kind, message.ImgPath)
    };

    public static ChatBubble FromWorkspace(WorkspaceMessage message, bool showDay, string dayLabel) => new()
    {
        Source = message,
        LocalId = message.LocalId,
        IsOutgoing = message.IsOutgoing,
        DayLabel = dayLabel,
        ShowDay = showDay,
        Sender = message.Sender,
        ShowSender = !message.IsOutgoing &&
                     message.ConversationId.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrWhiteSpace(message.Sender),
        Time = ShortTime(message.CreateTime),
        KindLabel = KindLabelFor(message.Kind),
        Body = ChatExportService.Describe(new WeChatMessage
        {
            Kind = message.Kind,
            Content = message.Content,
            ImgPath = message.Attachment,
            IsOutgoing = message.IsOutgoing
        }),
        IsTransaction = message.IsTransaction,
        IsNew = message.IsNew,
        IsDirty = message.IsDirty,
        LocalImagePath = LocalImage(message.Kind, message.Attachment),
        AttachmentNote = AttachmentNoteFor(message.Kind, message.Attachment)
    };

    private static string KindLabelFor(MessageKind kind) => kind switch
    {
        MessageKind.Text => "",
        _ => kind.ToString()
    };

    private static string? LocalImage(MessageKind kind, string? path)
    {
        if (kind is not (MessageKind.Image or MessageKind.Emoji)) return null;
        if (string.IsNullOrWhiteSpace(path)) return null;
        return File.Exists(path) ? path : null;
    }

    private static string AttachmentNoteFor(MessageKind kind, string? path)
    {
        if (!MessageKindPolicy.HasExternalMedia(kind)) return "";
        if (string.IsNullOrWhiteSpace(path)) return "未找到附件";
        return File.Exists(path) ? "" : "附件在手机端";
    }

    private static string ShortTime(long unixSeconds)
    {
        if (unixSeconds <= 0) return "";
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds)
                .LocalDateTime.ToString("HH:mm");
        }
        catch (ArgumentOutOfRangeException)
        {
            return "";
        }
    }
}
