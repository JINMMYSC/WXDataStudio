using System.IO;
using System.Text;
using System.Text.Json;
using WXDataStudio.App.Models;

namespace WXDataStudio.App.Services;

public sealed class MigrationReportExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<(string JsonPath, string TextPath)> ExportAsync(
        MigrationReadinessReport report, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var stamp = report.GeneratedAt.ToString("yyyyMMdd-HHmmss");
        var jsonPath = Path.Combine(outputDirectory, $"migration-readiness-{stamp}.json");
        var textPath = Path.Combine(outputDirectory, $"migration-readiness-{stamp}.txt");

        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, JsonOptions));
        var text = new StringBuilder()
            .AppendLine("WXDataStudio Migration Readiness Report")
            .AppendLine($"Generated: {report.GeneratedAt:O}")
            .AppendLine($"Status: {report.Status}")
            .AppendLine($"Conversations: {report.ConversationCount}")
            .AppendLine($"Messages: {report.MessageCount}")
            .AppendLine($"Unknown types: {report.UnknownMessageCount}")
            .AppendLine($"Transaction-class records: {report.TransactionMessageCount}")
            .AppendLine()
            .AppendLine("Checks:");

        foreach (var item in report.Items)
            text.AppendLine($"[{item.Severity}] {item.Code}: {item.Message}");

        await File.WriteAllTextAsync(textPath, text.ToString());
        return (jsonPath, textPath);
    }
}
