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
}
