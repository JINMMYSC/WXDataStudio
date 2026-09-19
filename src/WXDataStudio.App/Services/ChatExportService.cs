using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using WXDataStudio.App.Models;

namespace WXDataStudio.App.Services;

public enum AttachmentStatus
{
    /// <summary>No attachment is expected for this message class.</summary>
    None,
    /// <summary>Attachment exists as a local file and was copied into the export.</summary>
    LocalFile,
    /// <summary>Attachment only exists on the phone; the export records the reference.</summary>
    DevicePathOnly,
    /// <summary>An attachment is expected but no file was found.</summary>
    MissingFile
}

public sealed record ChatExportSummary(
    string DirectoryPath,
    string JsonPath,
    string HtmlPath,
    int MessageCount,
    int LocalAssetCount,
    int DeviceOnlyAttachmentCount,
    int MissingAttachmentCount,
    IReadOnlyList<MessageKindCount> KindCounts);

/// <summary>
/// Exports one conversation to a local HTML transcript plus a JSON payload.
/// The export is written where the caller asks and never leaves the machine;
/// all text is HTML-escaped and attachment copies keep their original bytes.
/// </summary>
public sealed class ChatExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly HashSet<char> InvalidNameChars =
        new(Path.GetInvalidFileNameChars());

    public async Task<ChatExportSummary> ExportAsync(
        string outputRoot,
        string conversationName,
        IReadOnlyList<WeChatMessage> messages,
        string? snapshotDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var directory = Path.Combine(outputRoot, $"{SafeName(conversationName)}-{stamp}");
        var assetDirectory = Path.Combine(directory, "assets");
        Directory.CreateDirectory(directory);

        var records = new List<ExportRecord>(messages.Count);
        var localAssets = 0;
        var deviceOnly = 0;
        var missing = 0;
        var assetIndex = 0;

        for (var i = 0; i < messages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = messages[i];
            var attachment = message.ImgPath;
            var status = AttachmentStatus.None;
            string? copiedName = null;
            if (!string.IsNullOrWhiteSpace(attachment) || MessageKindPolicy.HasExternalMedia(message.Kind))
            {
                if (!string.IsNullOrWhiteSpace(attachment) && File.Exists(attachment))
                {
                    Directory.CreateDirectory(assetDirectory);
                    assetIndex++;
                    copiedName = $"{assetIndex:0000}-{SafeName(Path.GetFileNameWithoutExtension(attachment))}" +
                                 Path.GetExtension(attachment);
                    File.Copy(attachment, Path.Combine(assetDirectory, copiedName), overwrite: true);
                    status = AttachmentStatus.LocalFile;
                    localAssets++;
                }
                else if (!string.IsNullOrWhiteSpace(attachment))
                {
                    status = AttachmentStatus.DevicePathOnly;
                    deviceOnly++;
                }
                else
                {
                    status = AttachmentStatus.MissingFile;
                    missing++;
                }
            }

            records.Add(new ExportRecord(
                i + 1,
                message,
                MessageMetadataParser.Parse(message.Kind, message.Content),
                Describe(message),
                status,
                copiedName));
        }

        var counts = records
            .Select(x => x.Message)
            .GroupBy(x => x.Kind)
            .Select(g => new MessageKindCount(
                g.Key, g.Count(), g.Count(x => x.IsOutgoing), g.Count(x => !x.IsOutgoing)))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Kind)
            .ToArray();

        var jsonPath = Path.Combine(directory, "conversation.json");
        await File.WriteAllTextAsync(jsonPath, BuildJson(
            conversationName, snapshotDirectory, records, counts, localAssets, deviceOnly, missing),
            cancellationToken);

        var htmlPath = Path.Combine(directory, "conversation.html");
        await File.WriteAllTextAsync(htmlPath,
            BuildHtml(conversationName, snapshotDirectory, records, counts, localAssets, deviceOnly, missing),
            cancellationToken);

        return new ChatExportSummary(
            directory, jsonPath, htmlPath, records.Count, localAssets, deviceOnly, missing, counts);
    }

    private static string BuildJson(
        string conversationName,
        string? snapshotDirectory,
        IReadOnlyList<ExportRecord> records,
        IReadOnlyList<MessageKindCount> counts,
        int localAssets,
        int deviceOnly,
        int missing)
    {
        var payload = new
        {
            exportVersion = 1,
            exportedAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
            conversation = conversationName,
            snapshotDirectory,
            messageCount = records.Count,
            attachments = new
            {
                localFiles = localAssets,
                deviceOnlyReferences = deviceOnly,
                missing = missing
            },
            kindCounts = counts.Select(x => new
            {
                kind = x.Kind.ToString(),
                count = x.Count,
                outgoing = x.Outgoing,
                incoming = x.Incoming
            }),
            messages = records.Select(x => new
            {
                index = x.Index,
                localId = x.Message.LocalId,
                serverId = x.Message.ServerId,
                timeUnix = x.Message.CreateTime,
                time = x.Message.DisplayTime,
                direction = x.Message.IsOutgoing ? "outgoing" : "incoming",
                sender = x.Message.Sender,
                kind = x.Message.Kind.ToString(),
                rawType = x.Message.RawType,
                readOnly = x.Message.Sensitive,
                text = x.Description,
                content = x.Message.Content,
                attachment = x.CopiedAssetName ?? x.Message.ImgPath,
                attachmentStatus = x.AttachmentStatus.ToString(),
                metadata = new
                {
                    x.Metadata.Title,
                    x.Metadata.Description,
                    x.Metadata.Url,
                    x.Metadata.Address,
                    x.Metadata.FileExtension,
                    x.Metadata.FileSize,
                    x.Metadata.QuoteSender,
                    x.Metadata.QuoteContent,
                    x.Metadata.MiniProgramUserName,
                    x.Metadata.ContactNickName,
                    x.Metadata.TransactionAmount,
                    x.Metadata.TransactionStatus
                }
            })
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static string BuildHtml(
        string conversationName,
        string? snapshotDirectory,
        IReadOnlyList<ExportRecord> records,
        IReadOnlyList<MessageKindCount> counts,
        int localAssets,
        int deviceOnly,
        int missing)
    {
        var html = new StringBuilder();
        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine("<html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
        html.AppendLine($"<title>{E(conversationName)} · 聊天记录导出</title>");
        html.AppendLine("<style>");
        html.AppendLine("body{font-family:'Segoe UI','Microsoft YaHei',sans-serif;background:#f4f5f7;margin:0;padding:24px;}");
        html.AppendLine(".wrap{max-width:920px;margin:0 auto;}");
        html.AppendLine("header{background:#fff;border:1px solid #e2e4e8;border-radius:8px;padding:16px 18px;margin-bottom:16px;}");
        html.AppendLine("h1{font-size:18px;margin:0 0 8px;}");
        html.AppendLine(".summary{color:#5a6472;font-size:13px;line-height:1.7;}");
        html.AppendLine(".day{margin:18px 0 8px;color:#6a737d;font-size:12px;text-align:center;}");
        html.AppendLine(".msg{background:#fff;border:1px solid #e2e4e8;border-radius:8px;padding:10px 12px;margin:8px 0;max-width:78%;}");
        html.AppendLine(".msg.out{margin-left:auto;background:#eaf2ff;border-color:#bbd4ff;}");
        html.AppendLine(".meta{font-size:11px;color:#6a737d;margin-bottom:4px;}");
        html.AppendLine(".body{white-space:pre-wrap;word-break:break-word;line-height:1.6;}");
        html.AppendLine(".att{margin-top:6px;font-size:12px;color:#123c8c;}");
        html.AppendLine(".ro{display:inline-block;margin-left:6px;padding:1px 6px;border-radius:4px;background:#fff8e1;color:#7a5a00;font-size:11px;}");
        html.AppendLine(".notice{margin-top:10px;padding:8px 10px;border-radius:6px;background:#fff8e1;color:#7a5a00;font-size:12px;}");
        html.AppendLine("img.thumb{max-width:220px;max-height:220px;border-radius:6px;display:block;margin-top:6px;}");
        html.AppendLine("</style></head><body><div class=\"wrap\">");
        html.AppendLine("<header>");
        html.AppendLine($"<h1>{E(conversationName)}</h1>");
        html.AppendLine("<div class=\"summary\">");
        html.AppendLine($"导出时间：{E(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))}<br>");
        html.AppendLine($"消息总数：{records.Count}<br>");
        html.AppendLine("分类：" + E(string.Join('、', counts.Select(x => $"{x.Kind} {x.Count}"))) + "<br>");
        html.AppendLine($"附件：本地复制 {localAssets} 个，仅在手机端 {deviceOnly} 个，缺失 {missing} 个");
        if (!string.IsNullOrWhiteSpace(snapshotDirectory))
            html.AppendLine($"<br>来源快照：{E(snapshotDirectory)}");
        html.AppendLine("</div>");
        html.AppendLine("<div class=\"notice\">本文件包含个人聊天内容，只保存在本机。交易、红包、收付款类记录为只读展示，不代表可写回手机。</div>");
        html.AppendLine("</header>");

        string? currentDay = null;
        foreach (var record in records)
        {
            var day = DayLabel(record.Message.CreateTime);
            if (day != currentDay)
            {
                currentDay = day;
                html.AppendLine($"<div class=\"day\">{E(day)}</div>");
            }

            var classes = record.Message.IsOutgoing ? "msg out" : "msg in";
            html.AppendLine($"<div class=\"{classes}\">");
            html.AppendLine($"<div class=\"meta\">{E(record.Message.DisplayTime)} · " +
                            $"{(record.Message.IsOutgoing ? "我发送" : "对方发送")} · {E(record.Message.Kind.ToString())}" +
                            (record.Message.Sensitive ? "<span class=\"ro\">只读</span>" : "") + "</div>");
            html.AppendLine($"<div class=\"body\">{E(record.Description)}</div>");

            if (record.CopiedAssetName is not null)
            {
                if (record.Message.Kind is MessageKind.Image or MessageKind.Emoji)
                    html.AppendLine($"<img class=\"thumb\" src=\"assets/{E(record.CopiedAssetName)}\" alt=\"attachment\">");
                html.AppendLine($"<div class=\"att\">附件：assets/{E(record.CopiedAssetName)}</div>");
            }
            else if (record.AttachmentStatus == AttachmentStatus.DevicePathOnly)
            {
                html.AppendLine("<div class=\"att\">附件仅存在于手机端，未复制到本机。</div>");
            }
            else if (record.AttachmentStatus == AttachmentStatus.MissingFile)
            {
                html.AppendLine("<div class=\"att\">未找到附件文件。</div>");
            }

            html.AppendLine("</div>");
        }

        html.AppendLine("</div></body></html>");
        return html.ToString();
    }

    /// <summary>Turns a message into readable text without dumping raw XML.</summary>
    public static string Describe(WeChatMessage message)
    {
        var content = message.Content ?? "";
        var metadata = MessageMetadataParser.Parse(message.Kind, content);
        switch (message.Kind)
        {
            case MessageKind.Text:
                return Clean(content);
            case MessageKind.Image: return "[图片]";
            case MessageKind.Voice: return "[语音]";
            case MessageKind.Video: return "[视频]";
            case MessageKind.Emoji: return "[表情]";
            case MessageKind.File:
                return Join("[文件]", metadata.Title, metadata.FileSize, metadata.FileExtension);
            case MessageKind.Link:
                return Join("[链接]", metadata.Title, metadata.Url, metadata.Description);
            case MessageKind.MiniProgram:
                return Join("[小程序]", metadata.Title, metadata.MiniProgramUserName, metadata.Url);
            case MessageKind.Location:
                return Join("[位置]", metadata.Address, metadata.Title,
                    string.IsNullOrWhiteSpace(metadata.Latitude) ? "" : $"{metadata.Latitude},{metadata.Longitude}");
            case MessageKind.ContactCard:
                return Join("[名片]", metadata.ContactNickName, metadata.ContactUserName, metadata.Title);
            case MessageKind.Quote:
                return Join("[引用]", metadata.QuoteSender, metadata.QuoteContent, metadata.Title);
            case MessageKind.Transfer:
                return Join("[转账 · 只读]", metadata.TransactionAmount, metadata.TransactionStatus, metadata.TransactionMemo);
            case MessageKind.RedPacket:
                return Join("[红包 · 只读]", metadata.TransactionAmount, metadata.TransactionMemo);
            case MessageKind.Payment:
                return Join("[收付款 · 只读]", metadata.TransactionAmount, metadata.TransactionStatus, metadata.TransactionMemo);
            case MessageKind.Call:
                return Join("[通话]", metadata.Title, Clean(content));
            case MessageKind.System:
                return Join("[系统消息]", metadata.Title, Clean(content));
            default:
                return Clean(content);
        }
    }

    private static string Join(string label, params string?[] parts)
    {
        var rest = parts
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => Clean(x!))
            .Where(x => x.Length > 0)
            .ToArray();
        return rest.Length == 0 ? label : label + " " + string.Join(" ", rest);
    }

    private static readonly string[] XmlPayloadPrefixes =
    {
        "<msg", "<sysmsg", "<appmsg", "<img", "<voicemsg", "<videomsg",
        "<emoji", "<location", "<payinfo", "<wcpayinfo"
    };

    /// <summary>
    /// Drops WeChat protocol XML payloads so an export never shows raw markup,
    /// while ordinary text that merely starts with '&lt;' is kept.
    /// </summary>
    private static string Clean(string value)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0) return "";
        if (XmlPayloadPrefixes.Any(prefix =>
                text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) &&
            text.Contains('>'))
            return "";
        return text.Length <= 4000 ? text : text[..4000] + "…";
    }

    private static string DayLabel(long unixSeconds)
    {
        if (unixSeconds <= 0) return "(无时间)";
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds)
                .LocalDateTime.ToString("yyyy-MM-dd dddd", CultureInfo.GetCultureInfo("zh-CN"));
        }
        catch (ArgumentOutOfRangeException)
        {
            return "(无时间)";
        }
    }

    private static string SafeName(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0) return "conversation";
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
            builder.Append(InvalidNameChars.Contains(c) || c is '/' or '\\' ? '_' : c);
        var result = builder.ToString().Trim().Trim('.');
        if (result.Length == 0) return "conversation";
        return result.Length <= 60 ? result : result[..60];
    }

    private static string E(string value) => WebUtility.HtmlEncode(value);

    private sealed record ExportRecord(
        int Index,
        WeChatMessage Message,
        MessageMetadata Metadata,
        string Description,
        AttachmentStatus AttachmentStatus,
        string? CopiedAssetName);
}
