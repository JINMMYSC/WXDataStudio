namespace WXDataStudio.App.Models;

public sealed record MediaCandidate(
    string RemotePath,
    string Role,
    bool Exists);

public sealed class MediaResolution
{
    public long MessageId { get; init; }
    public MessageKind Kind { get; init; }
    public IReadOnlyList<MediaCandidate> Candidates { get; init; } = Array.Empty<MediaCandidate>();
    public string Summary => Candidates.Count == 0
        ? "未定位到附件"
        : string.Join("; ", Candidates.Select(x => $"{x.Role}: {x.RemotePath}"));
}
