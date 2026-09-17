namespace WXDataStudio.App.Models;

public sealed record WorkspaceDiff(
    long MessageId,
    MessageKind Kind,
    string Field,
    string Before,
    string After);
