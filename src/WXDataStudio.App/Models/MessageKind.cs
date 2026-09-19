namespace WXDataStudio.App.Models;

public enum MessageKind
{
    Unknown = 0,
    Text,
    Image,
    Voice,
    Video,
    Emoji,
    Location,
    File,
    Link,
    MiniProgram,
    ContactCard,
    Quote,
    System,
    Transfer,
    RedPacket,
    Payment,
    Call
}

public static class MessageKindPolicy
{
    /// <summary>
    /// Transaction-class records. They are labelled in the UI because editing
    /// them changes financial-looking history, but they are editable: this tool
    /// exists to repair accidentally deleted personal chat records.
    /// </summary>
    public static bool IsTransaction(MessageKind kind) => kind is
        MessageKind.Transfer or MessageKind.RedPacket or MessageKind.Payment;

    /// <summary>Every message class can be created or edited in a workspace.</summary>
    public static bool IsEditable(MessageKind kind) => true;

    public static bool HasExternalMedia(MessageKind kind) => kind is
        MessageKind.Image or MessageKind.Voice or MessageKind.Video or
        MessageKind.Emoji or MessageKind.File;
}
