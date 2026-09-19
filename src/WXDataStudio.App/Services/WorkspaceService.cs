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
        DateTimeOffset? when = null,
        bool isOutgoing = true,
        string sender = "")
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
            attachment,
            isOutgoing,
            sender);
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

    /// <summary>
    /// Removes a message from the conversation. Messages added in this session
    /// disappear immediately; messages that came from the phone are flagged so
    /// the write-back deletes them.
    /// </summary>
    public bool Delete(WorkspaceDocument workspace, long messageId)
    {
        var message = workspace.Messages.FirstOrDefault(x => x.LocalId == messageId);
        if (message is null) return false;
        if (message.IsNew)
        {
            workspace.Audit.Add(new WorkspaceAuditEntry
            {
                MessageId = message.LocalId,
                Field = "delete-new",
                Before = message.Kind.ToString(),
                After = ""
            });
            workspace.Messages.Remove(message);
            return true;
        }

        if (message.IsDeleted) return false;
        message.IsDeleted = true;
        workspace.Audit.Add(new WorkspaceAuditEntry
        {
            MessageId = message.LocalId,
            Field = "delete",
            Before = message.Content,
            After = ""
        });
        return true;
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

    /// <summary>
    /// Finds edits that were saved earlier for this conversation, so switching
    /// chats and coming back never loses what the user already changed.
    /// </summary>
    public async Task<WorkspaceDocument?> FindSavedAsync(
        string root, string? snapshotDirectory, string conversationId)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
        WorkspaceDocument? exact = null;
        WorkspaceDocument? newest = null;
        DateTime newestTime = DateTime.MinValue;
        foreach (var path in Directory.EnumerateFiles(root, "workspace-*.json"))
        {
            try
            {
                var document = await LoadAsync(path);
                if (!string.Equals(document.ConversationId, conversationId, StringComparison.Ordinal))
                    continue;
                if (exact is null &&
                    !string.IsNullOrWhiteSpace(snapshotDirectory) &&
                    !string.IsNullOrWhiteSpace(document.SourceSnapshotDirectory) &&
                    string.Equals(document.SourceSnapshotDirectory, snapshotDirectory,
                        StringComparison.OrdinalIgnoreCase))
                {
                    exact = document;
                }

                var written = File.GetLastWriteTime(path);
                if (written > newestTime)
                {
                    newestTime = written;
                    newest = document;
                }
            }
            catch
            {
                // A damaged workspace file must not block loading a conversation.
            }
        }
        return exact ?? newest;
    }

    /// <summary>
    /// Applies previously saved edits on top of freshly read messages. Reading
    /// the phone again produces a new snapshot, so the saved copy no longer
    /// matches by snapshot id; the pending changes are rebased onto the current
    /// conversation instead of being dropped.
    /// </summary>
    public WorkspaceDocument Rebase(
        WorkspaceDocument saved,
        string snapshotDirectory,
        ConversationItem conversation,
        IEnumerable<WeChatMessage> currentMessages)
    {
        var rebased = Create(snapshotDirectory, conversation, currentMessages);
        var byId = rebased.Messages.ToDictionary(x => x.LocalId);
        foreach (var previous in saved.Messages)
        {
            if (previous.IsNew)
            {
                rebased.Messages.Add(new WorkspaceMessage
                {
                    LocalId = rebased.Messages.Where(x => x.LocalId < 0)
                        .Select(x => x.LocalId).DefaultIfEmpty(0).Min() - 1,
                    ConversationId = rebased.ConversationId,
                    Sender = previous.Sender,
                    IsOutgoing = previous.IsOutgoing,
                    Kind = previous.Kind,
                    IsNew = true,
                    IsTransaction = previous.IsTransaction,
                    Content = previous.Content,
                    CreateTime = previous.CreateTime,
                    Attachment = previous.Attachment
                });
                continue;
            }

            if (!byId.TryGetValue(previous.LocalId, out var current)) continue;
            if (previous.IsDeleted)
            {
                current.IsDeleted = true;
                continue;
            }
            if (previous.Content != previous.OriginalContent)
                current.Content = previous.Content;
            if (previous.CreateTime != previous.OriginalCreateTime)
                current.CreateTime = previous.CreateTime;
            if (!string.Equals(previous.Attachment, previous.OriginalAttachment, StringComparison.Ordinal))
                current.Attachment = previous.Attachment;
        }

        rebased.Messages.Sort((a, b) =>
        {
            var byTime = a.CreateTime.CompareTo(b.CreateTime);
            return byTime != 0 ? byTime : a.LocalId.CompareTo(b.LocalId);
        });
        rebased.Audit.Add(new WorkspaceAuditEntry
        {
            MessageId = 0,
            Field = "rebase",
            Before = saved.SourceSnapshotDirectory,
            After = snapshotDirectory
        });
        return rebased;
    }

    private static WorkspaceMessage GetEditable(WorkspaceDocument workspace, long id)
    {
        var msg = workspace.Messages.FirstOrDefault(x => x.LocalId == id)
            ?? throw new KeyNotFoundException($"Message {id} was not found.");
        if (!msg.CanEdit)
            throw new InvalidOperationException("This workspace message cannot be edited.");
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
