namespace WXDataStudio.App.Services;

/// <summary>
/// Compatibility facade over <see cref="LegacySc1DatabaseService"/> for callers
/// that only need the derived plaintext path. Decryption now folds the snapshot's
/// write-ahead log, so the derived database matches what WeChat displays.
/// </summary>
public sealed class LegacySc1PageDecryptService
{
    private readonly LegacySc1DatabaseService _inner = new();

    public bool MatchesPassword(string databasePath, string password) =>
        _inner.MatchesPassword(databasePath, password);

    public bool MatchesRawKey(string databasePath, string rawKeyHex) =>
        _inner.MatchesRawKey(databasePath, rawKeyHex);

    public async Task<string> DecryptWithPasswordAsync(
        string databasePath, string password, string outputPath) =>
        (await _inner.DecryptWithPasswordAsync(databasePath, password, outputPath)).OutputPath;

    public async Task<string> DecryptWithRawKeyAsync(
        string databasePath, string rawKeyHex, string outputPath) =>
        (await _inner.DecryptWithRawKeyAsync(databasePath, rawKeyHex, outputPath)).OutputPath;
}
