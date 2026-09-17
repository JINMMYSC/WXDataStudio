using WXDataStudio.App.Models;

namespace WXDataStudio.App.Services;

public sealed record DatabaseCredentialResolution(
    bool Success,
    string? Password,
    int CipherCompatibility,
    string Source,
    string Message);

public sealed class DatabaseCredentialResolver
{
    private readonly WeChatDatabaseReader _reader;
    private readonly LegacyWeChatKeyCandidateService _candidates;

    public DatabaseCredentialResolver(
        WeChatDatabaseReader reader,
        LegacyWeChatKeyCandidateService candidates)
    {
        _reader = reader;
        _candidates = candidates;
    }

    public async Task<DatabaseCredentialResolution> ResolveAsync(string databasePath)
    {
        var candidates = await _candidates.BuildAsync();
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
                var tables = await _reader.ListTablesAsync(databasePath, legacy);
                if (tables.Contains("message", StringComparer.OrdinalIgnoreCase) ||
                    tables.Contains("rconversation", StringComparer.OrdinalIgnoreCase))
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

                    var tables = await _reader.ListTablesAsync(databasePath, options);
                    if (tables.Count == 0) continue;
                    if (!tables.Contains("message", StringComparer.OrdinalIgnoreCase) &&
                        !tables.Contains("rconversation", StringComparer.OrdinalIgnoreCase))
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
        }

        return new(false, null, 0, "none",
            "None of the bounded device-derived candidates opened the database.");
    }
}
