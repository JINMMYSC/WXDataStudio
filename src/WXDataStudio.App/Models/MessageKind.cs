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
    public static bool IsSensitive(MessageKind kind) => kind is
        MessageKind.Transfer or MessageKind.RedPacket or MessageKind.Payment;

    public static bool IsEditable(MessageKind kind) => !IsSensitive(kind) && kind is not
        MessageKind.Unknown and not MessageKind.System;

    public static bool HasExternalMedia(MessageKind kind) => kind is
        MessageKind.Image or MessageKind.Voice or MessageKind.Video or
        MessageKind.Emoji or MessageKind.File;
}
