namespace WXDataStudio.App.Models;

public enum MigrationCheckSeverity
{
    Info,
    Warning,
    Error
}

public sealed record MigrationCheckItem(
    string Code,
    MigrationCheckSeverity Severity,
    string Message);

public sealed class MigrationReadinessReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.Now;
    public int ConversationCount { get; init; }
    public int MessageCount { get; init; }
    public int SensitiveMessageCount { get; init; }
    public int UnknownMessageCount { get; init; }
    public IReadOnlyList<MigrationCheckItem> Items { get; init; } = Array.Empty<MigrationCheckItem>();

    public bool IsBlocked => Items.Any(x => x.Severity == MigrationCheckSeverity.Error);
    public int WarningCount => Items.Count(x => x.Severity == MigrationCheckSeverity.Warning);
    public string Status => IsBlocked ? "Blocked" : WarningCount > 0 ? "Review" : "Ready";
}
