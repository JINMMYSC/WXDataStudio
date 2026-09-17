namespace WXDataStudio.App.Models;

public sealed class DatabaseOpenOptions
{
    public string? Password { get; init; }
    public string? RawKeyHex { get; init; }
    public int CipherCompatibility { get; init; } = 1;
    public bool UseLegacyWeChatCipher { get; init; }
    public bool ReadOnly { get; init; } = true;
}

public sealed class DatabaseInspection
{
    public string Path { get; init; } = "";
    public long Size { get; init; }
    public bool IsPlainSqlite { get; init; }
    public bool AppearsEncrypted { get; init; }
    public string HeaderHex { get; init; } = "";
    public string Status { get; init; } = "";
}
