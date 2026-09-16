namespace WXDataStudio.App.Models;

public sealed record DeviceInfo
{
    public string Serial { get; init; } = "";
    public string Model { get; init; } = "";
    public string Codename { get; init; } = "";
    public string AndroidVersion { get; init; } = "";
    public string Sdk { get; init; } = "";
    public string MiuiVersion { get; init; } = "";
    public string Abi { get; init; } = "";
    public bool RootAvailable { get; init; }
    public bool BootloaderUnlocked { get; init; }
    public string WeChatVersion { get; init; } = "";
    public string WeChatVersionCode { get; init; } = "";
    public string AccountDirectory { get; init; } = "";
    public string MainDatabasePath { get; init; } = "";

    public bool MatchesLockedBaseline =>
        Model.Contains("MIX 2S", StringComparison.OrdinalIgnoreCase)
        && AndroidVersion == "9"
        && MiuiVersion == "V10.3.5.0.PDGCNXM"
        && WeChatVersion == "8.0.76"
        && WeChatVersionCode == "3141"
        && RootAvailable;
}
