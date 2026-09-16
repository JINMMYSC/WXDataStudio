namespace WXDataStudio.App.Models;

public sealed class WorkspaceDocument
{
    public string WorkspaceId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public string SourceSnapshotDirectory { get; init; } = "";
    public string ConversationId { get; init; } = "";
    public string ConversationName { get; init; } = "";
    public List<WorkspaceMessage> Messages { get; init; } = [];
    public List<WorkspaceAuditEntry> Audit { get; init; } = [];
}

public sealed class WorkspaceAuditEntry
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
    public long MessageId { get; init; }
    public string Field { get; init; } = "";
    public string Before { get; init; } = "";
    public string After { get; init; } = "";
}
