namespace WXDataStudio.App.Models;

public sealed class MessageMetadata
{
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Url { get; init; } = "";
    public string AppId { get; init; } = "";
    public string Address { get; init; } = "";
    public string Latitude { get; init; } = "";
    public string Longitude { get; init; } = "";
    public string FileExtension { get; init; } = "";
    public string FileSize { get; init; } = "";
    public string TransactionType { get; init; } = "";
    public string TransactionAmount { get; init; } = "";
    public string TransactionStatus { get; init; } = "";
    public string TransactionMemo { get; init; } = "";
}
