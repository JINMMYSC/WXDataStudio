using System.IO;
using WXDataStudio.App.Models;

namespace WXDataStudio.App.Services;

public sealed record DatabaseCredentialResolution(
    bool Success,
    string? Password,
    int CipherCompatibility,
    string Source,
    string Message,
    string? DecryptedPath = null);

public sealed class DatabaseCredentialResolver
{
    private readonly WeChatDatabaseReader _reader;
    private readonly LegacyWeChatKeyCandidateService _candidates;
    private readonly LegacySc1PageDecryptService _sc1 = new();

    public DatabaseCredentialResolver(
        WeChatDatabaseReader reader,
        LegacyWeChatKeyCandidateService candidates)
    {
        _reader = reader;
        _candidates = candidates;
    }

    public async Task<DatabaseCredentialResolution> ResolveAsync(
        string databasePath,
        string? snapshotDirectory = null,
        string? derivedDirectory = null)
    {
        // Candidate discovery reads the snapshot's support files, while the database
        // itself is expected to be a read-session working copy of that snapshot.
        snapshotDirectory ??= Path.GetDirectoryName(databasePath);
        var candidates = await _candidates.BuildAsync(snapshotDirectory);
        if (candidates.Count == 0)
            return new(false, null, 0, "none",
                "No bounded device-derived key candidates were available.");
        foreach (var candidate in candidates)
        {
            try
            {
                var legacy = new DatabaseOpenOptions
                {
                    Password = candidate.Password,
                    UseLegacyWeChatCipher = true,
                    ReadOnly = true
                };
                var schema = await _reader.DetectSchemaAsync(databasePath, legacy);
                if (schema.MessageTable is not null || schema.ConversationTable is not null)
                    return new(true, candidate.Password, 0, candidate.Source,
                        "Database opened with WeChat legacy SQLCipher profile.");
            }
            catch
            {
                // Try standard SQLCipher compatibility profiles next.
            }

            foreach (var compatibility in new[] { 1, 3, 4 })
            {
                try
                {
                    var options = new DatabaseOpenOptions
                    {
                        Password = candidate.Password,
                        CipherCompatibility = compatibility,
                        ReadOnly = true
                    };

                    var schema = await _reader.DetectSchemaAsync(databasePath, options);
                    if (schema.MessageTable is null && schema.ConversationTable is null)
                        continue;

                    return new(true, candidate.Password, compatibility,
                        candidate.Source,
                        $"Database opened with compatibility {compatibility}.");
                }
                catch
                {
                    // Candidate mismatch. Continue with the bounded local set.
                }
            }

            try
            {
                if (_sc1.MatchesPassword(databasePath, candidate.Password))
                {
                    var derivedDir = derivedDirectory ?? Path.Combine(
                        Path.GetDirectoryName(databasePath) ?? ".", "derived");
                    Directory.CreateDirectory(derivedDir);
                    var output = Path.Combine(derivedDir, "EnMicroMsg.sc1.decrypted.db");
                    await _sc1.DecryptWithPasswordAsync(databasePath, candidate.Password, output);
                    var schema = await _reader.DetectSchemaAsync(
                        output, new DatabaseOpenOptions { ReadOnly = true });
                    if (schema.MessageTable is not null || schema.ConversationTable is not null)
                    {
                        return new(true, candidate.Password, 0,
                            candidate.Source + ":sc1-page",
                            "Database was opened through a derived read-only SC1 plaintext copy.",
                            output);
                    }
                }
            }
            catch
            {
                // Derived fallback is best-effort; continue the bounded candidate set.
            }
        }

        return new(false, null, 0, "none",
            "None of the bounded device-derived candidates opened the database.");
    }
}
