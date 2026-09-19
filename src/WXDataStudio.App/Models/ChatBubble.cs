using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using WXDataStudio.App.Services;

namespace WXDataStudio.App.Models;

/// <summary>
/// One chat row. It keeps the original message in <see cref="Source"/> so the
/// window can save edits, and it is observable so a bubble can switch into an
/// in-place editor the way WeChat does.
/// </summary>
public sealed class ChatBubble : INotifyPropertyChanged
{
    private bool _isEditing;
    private string _editText = "";
    private string _editTime = "";
    private string _body = "";
    private string _time = "";

    public object Source { get; init; } = default!;
    public long LocalId { get; init; }
    public bool IsOutgoing { get; init; }
    public bool CanEdit { get; init; }
    public bool HasMediaSlot { get; init; }
    public string DayLabel { get; init; } = "";
    public bool ShowDay { get; init; }
    public string Sender { get; init; } = "";
    public bool ShowSender { get; init; }
    public string Time
    {
        get => _time;
        set
        {
            _time = value;
            OnPropertyChanged();
        }
    }
    public string KindLabel { get; init; } = "";
    public bool IsTransaction { get; init; }
    public bool IsTransfer { get; init; }
    public bool IsRedPacket { get; init; }
    public bool IsPayment { get; init; }
    public string CardTitle { get; init; } = "";
    public string CardAmount { get; init; } = "";
    public string CardStatus { get; init; } = "";
    public bool HasCardAmount => !string.IsNullOrWhiteSpace(CardAmount);
    public bool HasCardStatus => !string.IsNullOrWhiteSpace(CardStatus);
    public bool IsNew { get; init; }
    public bool IsDirty { get; init; }
    public bool IsDeleted { get; set; }
    private string? _localImagePath;
    public string? LocalImagePath
    {
        get => _localImagePath;
        set
        {
            _localImagePath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasLocalImage));
        }
    }
    private string _attachmentNote = "";
    public string AttachmentNote
    {
        get => _attachmentNote;
        set
        {
            _attachmentNote = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasAttachmentNote));
        }
    }
    public bool HasLocalImage => !string.IsNullOrWhiteSpace(LocalImagePath);
    public bool HasAttachmentNote => !string.IsNullOrWhiteSpace(AttachmentNote);
    public bool HasKindLabel => !string.IsNullOrWhiteSpace(KindLabel);

    public string Body
    {
        get => _body;
        set
        {
            _body = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayBody));
        }
    }

    public string DisplayBody => IsDeleted ? "(这条记录已删除)" : Body;

    /// <summary>True when the bubble is showing its in-place editor.</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value) return;
            _isEditing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsViewing));
        }
    }

    public bool IsViewing => !_isEditing;

    /// <summary>Text being typed inside the bubble editor.</summary>
    public string EditText
    {
        get => _editText;
        set
        {
            _editText = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Time being typed inside the bubble editor (empty leaves it as is).</summary>
    public string EditTime
    {
        get => _editTime;
        set
        {
            _editTime = value;
            OnPropertyChanged();
        }
    }

    public string CreateTimeText { get; init; } = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public static ChatBubble FromMessage(WeChatMessage message, bool showDay, string dayLabel) => new()
    {
        Source = message,
        LocalId = message.LocalId,
        IsOutgoing = message.IsOutgoing,
        CanEdit = false,
        HasMediaSlot = MessageKindPolicy.HasExternalMedia(message.Kind),
        DayLabel = dayLabel,
        ShowDay = showDay,
        Sender = message.Sender,
        ShowSender = ShowsSender(message.ConversationId, message.IsOutgoing, message.Sender),
        Time = ShortTime(message.CreateTime),
        CreateTimeText = FormatTime(message.CreateTime),
        KindLabel = KindLabelFor(message.Kind),
        Body = ChatExportService.Describe(message),
        IsTransaction = message.IsTransaction,
        IsTransfer = message.Kind == MessageKind.Transfer,
        IsRedPacket = message.Kind == MessageKind.RedPacket,
        IsPayment = message.Kind == MessageKind.Payment,
        CardTitle = CardTitleFor(message.Kind),
        CardAmount = TransactionMessageTemplate.Read(message.Kind, message.Content).Amount,
        CardStatus = TransactionMessageTemplate.Read(message.Kind, message.Content).Status,
        LocalImagePath = LocalImage(message.Kind, message.ImgPath),
        AttachmentNote = AttachmentNoteFor(message.Kind, message.ImgPath)
    };

    public static ChatBubble FromWorkspace(WorkspaceMessage message, bool showDay, string dayLabel) => new()
    {
        Source = message,
        LocalId = message.LocalId,
        IsOutgoing = message.IsOutgoing,
        CanEdit = message.CanEdit,
        HasMediaSlot = MessageKindPolicy.HasExternalMedia(message.Kind),
        DayLabel = dayLabel,
        ShowDay = showDay,
        Sender = message.Sender,
        ShowSender = ShowsSender(message.ConversationId, message.IsOutgoing, message.Sender),
        Time = ShortTime(message.CreateTime),
        CreateTimeText = FormatTime(message.CreateTime),
        KindLabel = KindLabelFor(message.Kind),
        Body = ChatExportService.Describe(new WeChatMessage
        {
            Kind = message.Kind,
            Content = message.Content,
            ImgPath = message.Attachment,
            IsOutgoing = message.IsOutgoing
        }),
        IsTransaction = message.IsTransaction,
        IsTransfer = message.Kind == MessageKind.Transfer,
        IsRedPacket = message.Kind == MessageKind.RedPacket,
        IsPayment = message.Kind == MessageKind.Payment,
        CardTitle = CardTitleFor(message.Kind),
        CardAmount = TransactionMessageTemplate.Read(message.Kind, message.Content).Amount,
        CardStatus = TransactionMessageTemplate.Read(message.Kind, message.Content).Status,
        IsNew = message.IsNew,
        IsDirty = message.IsDirty,
        IsDeleted = message.IsDeleted,
        LocalImagePath = LocalImage(message.Kind, message.Attachment),
        AttachmentNote = AttachmentNoteFor(message.Kind, message.Attachment)
    };

    private static bool ShowsSender(string conversationId, bool isOutgoing, string sender) =>
        !isOutgoing &&
        conversationId.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(sender);

    private static string KindLabelFor(MessageKind kind) => kind switch
    {
        MessageKind.Text => "",
        _ => kind.ToString()
    };

    private static string CardTitleFor(MessageKind kind) => kind switch
    {
        MessageKind.Transfer => "转账",
        MessageKind.RedPacket => "微信红包",
        MessageKind.Payment => "收付款",
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

    /// <summary>Full timestamp shown in the editor so it can be changed.</summary>
    public static string FormatTime(long unixSeconds)
    {
        if (unixSeconds <= 0) return "";
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds)
                .LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        }
        catch (ArgumentOutOfRangeException)
        {
            return "";
        }
    }

    /// <summary>
    /// Accepts the formats people actually type: with or without seconds, with
    /// '-' or '/', and month-day only (current year is assumed).
    /// </summary>
    public static bool TryParseTime(string? text, out long unixSeconds)
    {
        unixSeconds = 0;
        var value = (text ?? "").Trim();
        if (value.Length == 0) return false;
        string[] formats =
        {
            "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd HH:mm",
            "yyyy-M-d H:m:s", "yyyy-M-d H:m", "yyyy-MM-dd'T'HH:mm:ss", "MM-dd HH:mm", "MM-dd HH:mm:ss"
        };
        if (DateTime.TryParseExact(value, formats, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
        {
            // "MM-dd HH:mm" carries no year; assume the current one.
            if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"\d{4}"))
                parsed = new DateTime(DateTime.Today.Year, parsed.Month, parsed.Day,
                    parsed.Hour, parsed.Minute, parsed.Second);
            unixSeconds = new DateTimeOffset(parsed, TimeZoneInfo.Local.GetUtcOffset(parsed))
                .ToUnixTimeSeconds();
            return true;
        }
        if (DateTime.TryParse(value, out parsed))
        {
            unixSeconds = new DateTimeOffset(parsed, TimeZoneInfo.Local.GetUtcOffset(parsed))
                .ToUnixTimeSeconds();
            return true;
        }
        return false;
    }
}
