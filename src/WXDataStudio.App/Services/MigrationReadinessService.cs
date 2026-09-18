using System.IO;
using WXDataStudio.App.Models;

namespace WXDataStudio.App.Services;

public sealed class MigrationReadinessService
{
    private readonly TimelineValidationService _timeline = new();

    public MigrationReadinessReport Evaluate(
        string snapshotDirectory,
        IReadOnlyList<ConversationItem> conversations,
        IReadOnlyList<WeChatMessage> messages,
        IReadOnlyList<string> snapshotIntegrityIssues)
    {
        var items = new List<MigrationCheckItem>();
        var database = Path.Combine(snapshotDirectory, "EnMicroMsg.db");

        if (!File.Exists(database) || new FileInfo(database).Length == 0)
            items.Add(new("database-missing", MigrationCheckSeverity.Error,
                "EnMicroMsg.db is missing or empty."));
        foreach (var issue in snapshotIntegrityIssues)
            items.Add(new("snapshot-integrity", MigrationCheckSeverity.Error, issue));

        if (conversations.Count == 0)
            items.Add(new("no-conversations", MigrationCheckSeverity.Warning,
                "No conversations have been loaded from the snapshot."));
        if (messages.Count == 0)
            items.Add(new("no-messages", MigrationCheckSeverity.Warning,
                "No messages are available for timeline validation."));

        items.AddRange(_timeline.Validate(messages));

        var unknown = messages.Count(x => x.Kind == MessageKind.Unknown);
        if (unknown > 0)
            items.Add(new("unknown-types", MigrationCheckSeverity.Warning,
                $"{unknown} message(s) use an unknown type and need review before migration."));

        var sensitive = messages.Count(x => x.Sensitive);
        if (sensitive > 0)
            items.Add(new("sensitive-readonly", MigrationCheckSeverity.Info,
                $"{sensitive} payment/red-packet/transfer record(s) are locked read-only."));

        if (!File.Exists(Path.Combine(snapshotDirectory, "EnMicroMsg.db-wal")))
            items.Add(new("wal-missing", MigrationCheckSeverity.Warning,
                "WAL file is not present in this snapshot."));
        if (!File.Exists(Path.Combine(snapshotDirectory, "EnMicroMsg.db-shm")))
            items.Add(new("shm-missing", MigrationCheckSeverity.Info,
                "SHM file is not present in this snapshot."));

        return new MigrationReadinessReport
        {
            ConversationCount = conversations.Count,
            MessageCount = messages.Count,
            SensitiveMessageCount = sensitive,
            UnknownMessageCount = unknown,
            Items = items
        };
    }
}
