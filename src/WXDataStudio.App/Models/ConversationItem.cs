namespace WXDataStudio.App.Models;

public sealed class ConversationItem
{
    public string Username { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Remark { get; init; } = "";
    public string NickName { get; init; } = "";
    public string LastContent { get; init; } = "";
    public long LastTime { get; init; }
    public bool IsGroup => Username.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase);
    public string EffectiveName => !string.IsNullOrWhiteSpace(Remark) ? Remark
        : !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName
        : !string.IsNullOrWhiteSpace(NickName) ? NickName
        : Username;
    public string LastDisplayTime => LastTime <= 0 ? "" : DateTimeOffset.FromUnixTimeSeconds(LastTime)
        .LocalDateTime.ToString("MM-dd HH:mm");
    public override string ToString() => string.IsNullOrWhiteSpace(LastDisplayTime)
        ? EffectiveName
        : $"{EffectiveName}   {LastDisplayTime}";
}
