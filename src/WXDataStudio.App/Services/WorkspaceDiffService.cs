using WXDataStudio.App.Models;

namespace WXDataStudio.App.Services;

public sealed class WorkspaceDiffService
{
    public IReadOnlyList<WorkspaceDiff> GetDiffs(WorkspaceDocument workspace)
    {
        var diffs = new List<WorkspaceDiff>();
        foreach (var m in workspace.Messages)
        {
            if (m.IsNew)
            {
                diffs.Add(new WorkspaceDiff(m.LocalId, m.Kind, "新增", "", m.Content));
                continue;
            }
            if (m.Content != m.OriginalContent)
                diffs.Add(new WorkspaceDiff(m.LocalId, m.Kind, "内容", m.OriginalContent, m.Content));
            if (m.CreateTime != m.OriginalCreateTime)
                diffs.Add(new WorkspaceDiff(m.LocalId, m.Kind, "时间",
                    Format(m.OriginalCreateTime), Format(m.CreateTime)));
            if (!string.Equals(m.Attachment, m.OriginalAttachment, StringComparison.Ordinal))
                diffs.Add(new WorkspaceDiff(m.LocalId, m.Kind, "附件",
                    m.OriginalAttachment ?? "", m.Attachment ?? ""));
        }
        return diffs;
    }

    private static string Format(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix)
        .LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
}
