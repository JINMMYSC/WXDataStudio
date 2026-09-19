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

    public WorkspaceMessage AddMessage(
        WorkspaceDocument workspace,
        MessageKind kind,
        string content,
        string? attachment = null,
        DateTimeOffset? when = null)
    {
        if (!MessageKindPolicy.IsEditable(kind))
            throw new InvalidOperationException("This message type cannot be created in a workspace.");

        var nextId = workspace.Messages.Where(x => x.LocalId < 0)
            .Select(x => x.LocalId).DefaultIfEmpty(0).Min() - 1;
        var message = WorkspaceMessage.CreateNew(
            nextId,
            workspace.ConversationId,
            kind,
            (when ?? DateTimeOffset.Now).ToUnixTimeSeconds(),
            content,
            attachment);
        workspace.Messages.Add(message);
        workspace.Messages.Sort((a, b) =>
        {
            var byTime = a.CreateTime.CompareTo(b.CreateTime);
            return byTime != 0 ? byTime : a.LocalId.CompareTo(b.LocalId);
        });
        workspace.Audit.Add(new WorkspaceAuditEntry
        {
            MessageId = message.LocalId,
            Field = "create",
            Before = "",
            After = kind.ToString()
        });
        return message;
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
        if (!string.IsNullOrWhiteSpace(path) && !MessageKindPolicy.HasExternalMedia(msg.Kind))
            throw new InvalidOperationException("This message type does not support external media replacement.");
        Audit(workspace, msg, "attachment", msg.Attachment ?? "", path ?? "");
        msg.Attachment = path;
    }

    public void Revert(WorkspaceDocument workspace, long messageId)
    {
        var msg = workspace.Messages.FirstOrDefault(x => x.LocalId == messageId)
            ?? throw new KeyNotFoundException($"Message {messageId} was not found.");
        if (msg.Sensitive) return;
        if (msg.IsNew)
        {
            workspace.Audit.Add(new WorkspaceAuditEntry
            {
                MessageId = msg.LocalId,
                Field = "delete-new",
                Before = msg.Kind.ToString(),
                After = ""
            });
            workspace.Messages.Remove(msg);
            return;
        }
        Audit(workspace, msg, "revert", "edited", "original");
        msg.Content = msg.OriginalContent;
        msg.CreateTime = msg.OriginalCreateTime;
        msg.Attachment = msg.OriginalAttachment;
    }

    /// <summary>
    /// Shifts the anchor message and every later message by the same offset so a
    /// new record can be inserted without disturbing the earlier timeline.
    /// Read-only transaction records are counted and skipped.
    /// </summary>
    public (int Shifted, int Skipped) ShiftTimeline(
        WorkspaceDocument workspace, long anchorMessageId, int offsetSeconds)
    {
        var ordered = workspace.Messages
            .OrderBy(x => x.CreateTime)
            .ThenBy(x => x.LocalId)
            .ToArray();
        var index = Array.FindIndex(ordered, x => x.LocalId == anchorMessageId);
        if (index < 0)
            throw new KeyNotFoundException($"Message {anchorMessageId} was not found.");

        var shifted = 0;
        var skipped = 0;
        foreach (var message in ordered.Skip(index))
        {
            if (!message.CanEdit)
            {
                skipped++;
                continue;
            }

            EditTime(workspace, message.LocalId, message.CreateTime + offsetSeconds);
            shifted++;
        }

        return (shifted, skipped);
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
