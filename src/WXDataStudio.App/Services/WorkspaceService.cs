using System.IO;
using System.Text.Json;
using WXDataStudio.App.Models;

namespace WXDataStudio.App.Services;

public sealed class WorkspaceService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public WorkspaceDocument Create(
        string snapshotDirectory,
        ConversationItem conversation,
        IEnumerable<WeChatMessage> messages)
    {
        return new WorkspaceDocument
        {
            SourceSnapshotDirectory = snapshotDirectory,
            ConversationId = conversation.Username,
            ConversationName = conversation.EffectiveName,
            Messages = messages.Select(WorkspaceMessage.From).ToList()
        };
    }

    public void EditContent(WorkspaceDocument workspace, long messageId, string newContent)
    {
        var msg = GetEditable(workspace, messageId);
        Audit(workspace, msg, "content", msg.Content, newContent);
        msg.Content = newContent;
    }

    public void EditTime(WorkspaceDocument workspace, long messageId, long unixSeconds)
    {
        var msg = GetEditable(workspace, messageId);
        Audit(workspace, msg, "createTime", msg.CreateTime.ToString(), unixSeconds.ToString());
        msg.CreateTime = unixSeconds;
    }

    public void EditAttachment(WorkspaceDocument workspace, long messageId, string? path)
    {
        var msg = GetEditable(workspace, messageId);
        Audit(workspace, msg, "attachment", msg.Attachment ?? "", path ?? "");
        msg.Attachment = path;
    }

    public void Revert(WorkspaceDocument workspace, long messageId)
    {
        var msg = workspace.Messages.FirstOrDefault(x => x.LocalId == messageId)
            ?? throw new KeyNotFoundException($"Message {messageId} was not found.");
        if (msg.Sensitive) return;
        Audit(workspace, msg, "revert", "edited", "original");
        msg.Content = msg.OriginalContent;
        msg.CreateTime = msg.OriginalCreateTime;
        msg.Attachment = msg.OriginalAttachment;
    }

    public async Task<string> SaveAsync(WorkspaceDocument workspace, string root)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, $"workspace-{workspace.WorkspaceId}.json");
        var json = JsonSerializer.Serialize(workspace, JsonOptions);
        await File.WriteAllTextAsync(path, json);
        return path;
    }

    public async Task<WorkspaceDocument> LoadAsync(string path)
    {
        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<WorkspaceDocument>(json, JsonOptions)
            ?? throw new InvalidDataException("Workspace JSON could not be parsed.");
    }

    private static WorkspaceMessage GetEditable(WorkspaceDocument workspace, long id)
    {
        var msg = workspace.Messages.FirstOrDefault(x => x.LocalId == id)
            ?? throw new KeyNotFoundException($"Message {id} was not found.");
        if (!msg.CanEdit)
            throw new InvalidOperationException("Sensitive payment-class records are read-only.");
        return msg;
    }

    private static void Audit(
        WorkspaceDocument workspace, WorkspaceMessage msg, string field, string before, string after)
    {
        if (before == after) return;
        workspace.Audit.Add(new WorkspaceAuditEntry
        {
            MessageId = msg.LocalId,
            Field = field,
            Before = before,
            After = after
        });
    }
}
