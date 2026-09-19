namespace WXDataStudio.App.Services;

public sealed record GroupMessageEnvelope(string Sender, string Body);

public static class GroupMessageParser
{
    public static GroupMessageEnvelope Parse(string? content)
    {
        var text = content ?? "";
        var marker = text.IndexOf(":\n", StringComparison.Ordinal);
        if (marker <= 0) return new("", text);

        var sender = text[..marker].Trim();
        if (!LooksLikeSender(sender)) return new("", text);
        return new(sender, text[(marker + 2)..]);
    }

    private static bool LooksLikeSender(string value)
    {
        if (value.Length is < 3 or > 128) return false;
        return value.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '@' or '.');
    }
}
